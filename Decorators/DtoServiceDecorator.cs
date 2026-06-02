using Jellyfin.Data.Enums;
using MediaBrowser.Model.MediaInfo;
using Jellyfin.Database.Implementations.Entities; // User
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Gelato.Decorators;

public sealed class DtoServiceDecorator(IDtoService inner, Lazy<GelatoManager> manager, IHttpContextAccessor http)
    : IDtoService
{
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly IHttpContextAccessor _http = http;
    private const int PrefetchConcurrency = 3;
    private const int PrefetchLimit = 20;

    public double? GetPrimaryImageAspectRatio(BaseItem item) =>
        inner.GetPrimaryImageAspectRatio(item);

    public BaseItemDto GetBaseItemDto(
        BaseItem item,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        // Force MediaSources to be included for Gelato Movie/Episode items so
        // that clients which don't explicitly request this field (e.g. Neptune,
        // Swiftfin) still receive the stream list and can show a version picker.
        if (
            item.IsGelato()
            && item.GetBaseItemKind() is BaseItemKind.Movie or BaseItemKind.Episode
            && !options.ContainsField(ItemFields.MediaSources)
        )
        {
            options.Fields = [.. options.Fields, ItemFields.MediaSources];
        }

        var dto = inner.GetBaseItemDto(item, options, user, owner);
        Patch(dto, item, _http.HttpContext?.IsApiListing() == true, user);
        return dto;
    }

    public IReadOnlyList<BaseItemDto> GetBaseItemDtos(
        IReadOnlyList<BaseItem> items,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        // im going to hell for this
        var item = items.FirstOrDefault();

        if (item != null && item.GetBaseItemKind() == BaseItemKind.BoxSet)
        {
            options.EnableUserData = false;
        }

        var list = inner.GetBaseItemDtos(items, options, user, owner);

        // Pre-warm stream cache in the background so that by the time the user
        // opens an item from this list, streams are already loaded and the detail
        // page opens instantly instead of waiting on the Stremio addon.
        if (user != null && _http.HttpContext?.IsApiListing() == true)
        {
            PrefetchStreamsInBackground(items, user.Id);
        }

        foreach (var itemDto in list)
        {
            Patch(itemDto, item, true, user);
        }
        return list;
    }

    private void PrefetchStreamsInBackground(IReadOnlyList<BaseItem> items, Guid userId)
    {
        var manager = _manager.Value;

        var toPrefetch = items
            .Where(i =>
                i.IsGelato()
                && i.GetBaseItemKind() is BaseItemKind.Movie or BaseItemKind.Episode
                && !manager.HasStreamSync($"{userId}:{i.Id}")
            )
            .Take(PrefetchLimit)
            .ToList();

        if (toPrefetch.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            using var throttle = new SemaphoreSlim(PrefetchConcurrency, PrefetchConcurrency);

            var tasks = toPrefetch.Select(async baseItem =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
                try
                {
                    var count = await manager
                        .SyncStreams(baseItem, userId, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (count > 0)
                        manager.SetStreamSync($"{userId}:{baseItem.Id}");
                }
                catch
                {
                    // Non-critical background prefetch — swallow silently
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        });
    }

    public BaseItemDto GetItemByNameDto(
        BaseItem item,
        DtoOptions options,
        List<BaseItem>? taggedItems,
        User? user = null
    )
    {
        var dto = inner.GetItemByNameDto(item, options, taggedItems, user);
        Patch(dto, item, _http.HttpContext?.IsApiListing() == true, user);
        return dto;
    }

    static bool IsGelato(BaseItemDto dto)
    {
        return dto.LocationType == LocationType.Remote
            && (
                dto.Type == BaseItemKind.Movie
                || dto.Type == BaseItemKind.Episode
                || dto.Type == BaseItemKind.Series
                || dto.Type == BaseItemKind.Season
            );
    }

    private void Patch(BaseItemDto dto, BaseItem? item, bool isList, User? user)
    {
        var manager = _manager.Value;
        if (item is not null && user is not null && IsGelato(dto) && manager.CanDelete(item, user))
        {
            dto.CanDelete = true;
        }

        if (IsGelato(dto))
        {
            if (dto.Path is not null && dto.Path.IsUrl())
            {
                // dto.Path = "/stub";


            }

            dto.CanDownload = true;
            // mark if placeholder
            if (
                isList
                || dto.MediaSources?.Length != 1
                || dto.Path is null
                || !dto.MediaSources[0]
                    .Path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase)
            )
            {
                if (dto.MediaSources != null)
                {
                    foreach (var source in dto.MediaSources)
                    {
                        //source.Path = "/stub";
                        //source.IsRemote = false;
                        // source.Protocol = MediaProtocol.File;
                    }
                }
                return;
            }

            dto.LocationType = LocationType.Virtual;
            dto.Path = null;
            dto.CanDownload = false;
        }
    }
}
