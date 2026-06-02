using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class InsertActionFilter(
    GelatoManager manager,
    IUserManager userManager,
    ILibraryManager libraryManager,
    ILogger<InsertActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    private readonly KeyLock _lock = new();
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var ct = ctx.HttpContext.RequestAborted;

        // Season-level blocking stream sync: when a client opens a season's episode
        // list, load streams for every episode in that season before responding so
        // that version selectors are immediately populated.
        if (
            ctx.GetActionName() == "GetEpisodes"
            && ctx.TryGetUserId(out var seasonUserId)
            && seasonUserId != Guid.Empty
        )
        {
            await SyncSeasonStreamsAsync(ctx, seasonUserId, ct).ConfigureAwait(false);
            await next();
            return;
        }

        if (
            !ctx.IsInsertableAction()
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
        )
        {
            await next();
            return;
        }

        // Handle local (non-gelato) series: sync or clean tree on demand
        if (libraryManager.GetItemById(guid) is Series localSeries && !localSeries.IsGelato())
        {
            await HandleLocalSeriesAsync(userId, localSeries, ct);
            await next();
            return;
        }

        // Item already exists in the library but stremio meta isn't in the
        // in-memory cache (e.g. server restart, cache eviction). Sync streams
        // directly before letting Jellyfin build the response.
        if (manager.GetStremioMeta(guid) is not { } stremioMeta)
        {
            if (libraryManager.GetItemById(guid) is Video existingVideo && existingVideo.IsGelato())
                await SyncItemStreamsIfNeededAsync(existingVideo, userId, ct).ConfigureAwait(false);

            await next();
            return;
        }

        // Get root folder
        var isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries
            ? manager.TryGetSeriesFolder(userId)
            : manager.TryGetMovieFolder(userId);
        if (root is null)
        {
            log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        if (manager.IntoBaseItem(stremioMeta) is { } item)
        {
            var existing = manager.FindExistingItem(item, user);
            if (existing is not null)
            {
                log.LogInformation(
                    "Media already exists; redirecting to canonical id {Id}",
                    existing.Id
                );
                ctx.ReplaceGuid(existing.Id);
                await SyncItemStreamsIfNeededAsync(existing, userId, ct).ConfigureAwait(false);
                await next();
                return;
            }
        }

        // Fetch full metadata
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var meta = await cfg.Stremio.GetMetaAsync(
            stremioMeta.ImdbId ?? stremioMeta.Id,
            stremioMeta.Type
        );
        if (meta is null)
        {
            log.LogError(
                "aio meta not found for {Id} {Type}, maybe try aiometadata as meta addon.",
                stremioMeta.Id,
                stremioMeta.Type
            );
            await next();
            return;
        }

        // Insert the item
        var baseItem = await InsertMetaAsync(guid, root, meta, user);
        if (baseItem is not null)
        {
            ctx.ReplaceGuid(baseItem.Id);
            manager.RemoveStremioMeta(guid);
            await SyncItemStreamsIfNeededAsync(baseItem, userId, ct).ConfigureAwait(false);
        }

        await next();
    }

    // Sync streams for a single movie or episode, blocking the response until done.
    private async Task SyncItemStreamsIfNeededAsync(BaseItem item, Guid userId, CancellationToken ct)
    {
        if (item is not Video video || !video.IsGelato() || video.IsStream())
            return;

        var cacheKey = $"{userId}:{item.Id}";
        if (manager.HasStreamSync(cacheKey))
            return;

        try
        {
            log.LogInformation("Blocking stream sync for {Kind} {Id}", item.GetBaseItemKind(), item.Id);
            var count = await manager.SyncStreams(item, userId, ct).ConfigureAwait(false);
            if (count > 0)
                manager.SetStreamSync(cacheKey);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Stream sync failed for {Id}", item.Id);
        }
    }

    // Sync streams for every episode in a season, blocking the response until all
    // are done. Runs up to 5 concurrent Stremio requests to keep it fast without
    // hammering the addon.
    private async Task SyncSeasonStreamsAsync(ActionExecutingContext ctx, Guid userId, CancellationToken ct)
    {
        var query = ctx.HttpContext.Request.Query;

        if (
            !query.TryGetValue("seasonId", out var seasonIdRaw)
            || !Guid.TryParse(seasonIdRaw, out var seasonId)
        )
            return;

        var episodes = libraryManager
            .GetItemList(new InternalItemsQuery
            {
                ParentId = seasonId,
                IncludeItemTypes = [BaseItemKind.Episode],
                IsDeadPerson = true,
            })
            .OfType<Episode>()
            .Where(e => e.IsGelato() && !e.IsStream() && !manager.HasStreamSync($"{userId}:{e.Id}"))
            .ToList();

        if (episodes.Count == 0)
            return;

        log.LogInformation(
            "Blocking season sync: {Count} episode(s) in season {SeasonId} need stream sync",
            episodes.Count,
            seasonId
        );

        using var throttle = new SemaphoreSlim(5, 5);

        var tasks = episodes.Select(async ep =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var count = await manager.SyncStreams(ep, userId, ct).ConfigureAwait(false);
                if (count > 0)
                    manager.SetStreamSync($"{userId}:{ep.Id}");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Stream sync failed for episode {Id}", ep.Id);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task HandleLocalSeriesAsync(Guid userId, Series series, CancellationToken ct)
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);

        if (cfg.ExtendLocalSeriesTrees)
        {
            var alreadySynced =
                series.Tags?.Contains(GelatoManager.TreeSyncedTag, StringComparer.OrdinalIgnoreCase)
                ?? false;
            if (alreadySynced)
                return;

            if (cfg.Stremio is not { } stremio)
                return;

            log.LogInformation(
                "InsertActionFilter: syncing local series tree for {Name} ({Id})",
                series.Name,
                series.Id
            );

            var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
            if (meta is null)
                return;

            await manager
                .SyncSeriesTreesAsync(cfg, meta, ct, existingSeries: series)
                .ConfigureAwait(false);
        }
        else
        {
            // Setting disabled — clean any virtual items that may exist for this series
            manager.CleanVirtualTreeItem(series, ct);
        }
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        User user
    )
    {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct =>
            {
                meta.Guid = guid;
                (baseItem, created) = await manager.InsertMeta(
                    root,
                    meta,
                    user,
                    false,
                    true,
                    meta.Type is StremioMediaType.Series,
                    ct
                );
            }
        );

        if (baseItem is not null && created)
            log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
