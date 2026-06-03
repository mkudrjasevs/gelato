using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Gelato.Providers;
using Gelato.Services;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class MediaSourceManagerDecorator(
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    GelatoItemRepository repo,
    IDirectoryService directoryService,
    IServerConfigurationManager config,
    //Lazy<ISubtitleManager> subtitleManager,
    Lazy<GelatoManager> manager,
    Lazy<SubtitleProvider> subtitleProvider,
    IMediaSegmentManager mediaSegmentManager,
    IEnumerable<ICustomMetadataProvider<Video>> videoProbeProviders
) : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ILogger<MediaSourceManagerDecorator> _log =
        log ?? throw new ArgumentNullException(nameof(log));
    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));
    private readonly KeyLock _lock = new();
    private readonly IMediaSegmentManager _mediaSegmentManager =
        mediaSegmentManager ?? throw new ArgumentNullException(nameof(mediaSegmentManager));
    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    private readonly IServerConfigurationManager _config =
        config ?? throw new ArgumentNullException(nameof(config));
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly Lazy<SubtitleProvider> _subtitleProvider = subtitleProvider;

    //  private readonly Lazy<ISubtitleManager> _subtitleManager = subtitleManager ?? throw new ArgumentNullException(nameof(subtitleManager));
    private readonly ICustomMetadataProvider<Video>? _probeProvider =
        videoProbeProviders.FirstOrDefault(p => p.Name == "Probe Provider");

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user = null
    )
    {
        var manager = _manager.Value;
        _log.LogDebug("GetStaticMediaSources {Id}", item.Id);
        var ctx = _http.HttpContext;
        Guid userId;
        if (user != null)
        {
            userId = user.Id;
        }
        else
        {
            ctx.TryGetUserId(out userId);
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            (!cfg.EnableMixed && !item.IsGelato())
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        }

        var uri = StremioUri.FromBaseItem(item);
        var actionName =
            ctx?.Items.TryGetValue("actionName", out var ao) == true ? ao as string : null;

        var allowSync = ctx.IsInsertableAction() && userId != Guid.Empty;
        var video = item as Video;
        var cacheKey = Guid.TryParse(video?.PrimaryVersionId, out var id)
            ? id.ToString()
            : item.Id.ToString();

        if (userId != Guid.Empty)
        {
            cacheKey = $"{userId.ToString()}:{cacheKey}";
        }

        if (!allowSync)
        {
            _log.LogDebug(
                "GetStaticMediaSources not a sync-eligible call. action={Action} uri={Uri}",
                actionName,
                uri?.ToString()
            );
        }
        else if (uri is not null && !manager.HasStreamSync(cacheKey))
        {
            // Bug in web UI that calls the detail page twice. So that's why there's a lock.
            _lock
                .RunSingleFlightAsync(
                    item.Id,
                    async ct =>
                    {
                        _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);

                        // Prewarm subtitle cache in the background if Gelato Subtitles
                        // is enabled for this library.
                        var libraryOptions = _libraryManager.GetLibraryOptions(item);
                        var subtitlePrewarmEnabled =
                            libraryOptions.SubtitleDownloadLanguages?.Length > 0
                            && !libraryOptions.DisabledSubtitleFetchers.Contains(
                                "Gelato Subtitles",
                                StringComparer.OrdinalIgnoreCase
                            );

                        if (subtitlePrewarmEnabled)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _subtitleProvider
                                        .Value.GetSubtitlesAsync(
                                            uri.ExternalId,
                                            uri.MediaType,
                                            CancellationToken.None
                                        )
                                        .ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _log.LogWarning(ex, "Subtitle prewarm failed for {Uri}", uri);
                                }
                            });
                        }

                        try
                        {
                            var count = await manager
                                .SyncStreams(item, userId, ct)
                                .ConfigureAwait(false);
                            if (count > 0)
                            {
                                manager.SetStreamSync(cacheKey);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Failed to sync streams");
                        }
                    }
                )
                .GetAwaiter()
                .GetResult();

            // refresh item
            libraryManager.GetItemById(item.Id);
        }

        // Strip sources that carry a non-HTTP scheme path (e.g. "gelato://stub/{id}" or
        // "stremio://…"). Jellyfin's inner implementation derives those paths directly
        // from the item's stored Path field, which Gelato sets to a virtual stub URI.
        // Letting them through causes HttpClient to throw NotSupportedException when
        // Jellyfin tries to fetch them as remote streams.
        var sources = _inner
            .GetStaticMediaSources(item, enablePathSubstitution, user)
            .Where(k =>
                string.IsNullOrEmpty(k.Path)
                || (!k.Path.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase)
                    && !k.Path.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
            )
            .ToList();

        // we dont use jellyfins alternate versions crap. So we have to load it ourselves

        InternalItemsQuery query;

        if (item.GetBaseItemKind() == BaseItemKind.Episode)
        {
            var episode = (Episode)item;
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                ParentId = episode.SeasonId,
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
                IndexNumber = episode.IndexNumber,
            };
        }
        else
        {
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                HasAnyProviderId = new Dictionary<string, string>
                {
                    { "Stremio", item.GetProviderId("Stremio") },
                },
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
            };
        }

        var gelatoSources = repo.GetItemList(query)
            .OfType<Video>()
            .Where(x =>
                x.IsGelato()
                && (
                    userId == Guid.Empty
                    || (x.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
                )
            )
            .OrderBy(x => x.GelatoData<int?>("index") ?? int.MaxValue)
            .Select(s =>
            {
                var k = GetVersionInfo(s, MediaSourceType.Grouping, user);

                if (user is not null)
                {
                    _inner.SetDefaultAudioAndSubtitleStreamIndices(item, k, user);
                }

                return k;
            })
            .ToList();

        _log.LogDebug(
            "Found {s} streams. UserId={Action} GelatoId={Uri}",
            gelatoSources.Count,
            userId,
            item.GetProviderId("Stremio")
        );

        sources.AddRange(gelatoSources);

        // When streams are present, remove any remaining stub/placeholder sources
        // (e.g. those derived from the item's own virtual path via inner sources).
        if (gelatoSources.Count > 0)
        {
            sources = sources
                .Where(k =>
                    !(k.Path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .Where(k =>
                    !(k.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .ToList();
        }

        // Failsafe: always return at least one source. Use a Placeholder (no path)
        // so Jellyfin shows the item but does not attempt to stream a broken URI.
        if (sources.Count == 0)
        {
            var placeholder = GetVersionInfo(item, MediaSourceType.Placeholder, user);
            placeholder.Path = null;
            sources.Add(placeholder);
        }

        if (sources.Count > 0)
            sources[0].Type = MediaSourceType.Default;

        sources[0].Id = item.Id.ToString("N");

        return sources;
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
    {
        _inner.AddParts(providers);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        return _inner.GetMediaStreams(itemId);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
    {
        return _inner.GetMediaStreams(query).ToList();
    }

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) =>
        _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) =>
        _inner.GetMediaAttachments(query);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken ct
    )
    {
        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        var manager = _manager.Value;
        var ctx = _http.HttpContext;

        var sources = GetStaticMediaSources(item, enablePathSubstitution, user);

        // Only honour a mediaSourceId that was explicitly supplied by the client.
        // Falling back to sources[0] meant clients that call GetPlaybackInfo
        // without a mediaSourceId (e.g. Neptune, Swiftfin) to discover available
        // versions only ever saw one source and had no picker to choose from.
        Guid? mediaSourceId =
            ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true
            && idObj is string idStr
            && Guid.TryParse(idStr, out var fromCtx)
                ? fromCtx
                : null;

        _log.LogDebug(
            "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId}",
            item.Id,
            mediaSourceId
        );

        // Identify the preferred source (explicit client choice, or first available).
        var selected = SelectByIdOrFirst(sources, mediaSourceId);
        if (selected is null)
            return sources;

        var owner = ResolveOwnerFor(selected, item);
        if (owner.IsPrimaryVersion() && owner.Id != item.Id)
        {
            sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(sources, mediaSourceId);
            if (selected is null)
                return sources;
        }

        if (NeedsProbe(selected))
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(owner);

            var segmentTask = _mediaSegmentManager.RunSegmentPluginProviders(
                owner,
                libraryOptions,
                false,
                ct
            );
            var metadataTask = ProbeStreamAsync((Video)owner, selected.Path, ct);

            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);

            await owner
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);

            var refreshed = GetStaticMediaSources(item, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(refreshed, mediaSourceId);

            if (selected is null)
                return refreshed;

            sources = refreshed.ToList();
        }

        if (item.RunTimeTicks is null && selected.RunTimeTicks is not null)
        {
            item.RunTimeTicks = selected.RunTimeTicks;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }

        // Always return ALL sources so every client can show a version picker,
        // regardless of whether it already sent a mediaSourceId. This fixes
        // TV episode playback on clients (e.g. Neptune) that read the first
        // available sourceId from the episode-list DTO and send it immediately,
        // which previously caused only one source to come back.
        //
        // Put the selected source first (Type=Default); the rest stay Grouping.
        // Stub ALL paths on POST so clients proxy through Jellyfin's stream
        // endpoint — the correct source is resolved there via mediaSourceId.
        var ordered = new List<MediaSourceInfo>(sources.Count) { selected };
        ordered.AddRange(sources.Where(s => s.Id != selected.Id));

        // Strip placeholder sources that have no path. A null-path source with
        // IsRemote=true causes ArgumentNullException inside Jellyfin's
        // GetStaticRemoteStreamResult when the client tries to stream it directly.
        var withPath = ordered.Where(s => !string.IsNullOrEmpty(s.Path)).ToList();
        if (withPath.Count == 0 && ordered.Count > 0)
        {
            // No real streams available yet — attempt a blocking on-demand sync
            // so the client gets actual sources instead of crashing Jellyfin.
            try
            {
                var syncCount = await manager.SyncStreams(item, user.Id, ct).ConfigureAwait(false);
                if (syncCount > 0)
                {
                    manager.SetStreamSync($"{user.Id}:{item.Id}");
                    var fresh = GetStaticMediaSources(item, enablePathSubstitution, user);
                    withPath = fresh.Where(s => !string.IsNullOrEmpty(s.Path)).ToList();
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "On-demand stream sync failed for {Id}", item.Id);
            }
        }
        if (withPath.Count > 0)
            ordered = withPath;

        if (ctx?.GetActionName() == "GetPostedPlaybackInfo")
        {
            foreach (var src in ordered)
            {
                // All sources in ordered now have non-null paths (filtered above),
                // so stub all of them unconditionally.
                src.Path = "/stub";
                src.IsRemote = false;
                src.Protocol = MediaProtocol.File;
            }
        }

        ordered[0].Type = MediaSourceType.Default;
        for (var i = 1; i < ordered.Count; i++)
            ordered[i].Type = MediaSourceType.Grouping;

        return ordered;

        static MediaSourceInfo? SelectByIdOrFirst(IReadOnlyList<MediaSourceInfo> list, Guid? id)
        {
            if (!id.HasValue)
                return list.FirstOrDefault();

            var target = id.Value;

            return list.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.Id) && Guid.TryParse(s.Id, out var g) && g == target
                ) ?? list.FirstOrDefault();
        }

        // Only probe sources that have a real streamable path.
        // A null/empty path means the source is a placeholder (no streams loaded yet);
        // probing it passes an empty string to FFmpeg which exits with code 254.
        static bool NeedsProbe(MediaSourceInfo s) =>
            !string.IsNullOrEmpty(s.Path)
            && (
                (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true)
                || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks
            );

        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            Guid.TryParse(s.ETag, out var g) ? libraryManager.GetItemById(g) ?? fallback : fallback;
    }

    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken
    ) =>
        _inner.GetMediaSource(
            item,
            mediaSourceId,
            liveStreamId,
            enablePathSubstitution,
            cancellationToken
        );

    public async Task<LiveStreamResponse> OpenLiveStream(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStream(request, cancellationToken);

    public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStream(id, cancellationToken);

    public Task<
        Tuple<MediaSourceInfo, IDirectStreamProvider>
    > GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) =>
        _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken
    ) => await _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    public async Task<MediaSourceInfo> GetLiveStreamMediaInfo(
        string id,
        CancellationToken cancellationToken
    ) => await _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) =>
        _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(
        BaseItem item,
        MediaSourceInfo source,
        User user
    ) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken
    ) =>
        _inner.AddMediaInfoWithProbe(
            mediaSource,
            isAudio,
            cacheKey,
            addProbeDelay,
            isLiveStream,
            cancellationToken
        );

    private MediaSourceInfo GetVersionInfo(
        BaseItem item,
        MediaSourceType type,
        User? user = null
    )
    {
        ArgumentNullException.ThrowIfNull(item);

        var streamName = item.GelatoData<string>("name");
        var streamDesc = item.GelatoData<string>("description");
        var bingeGroup = item.GelatoData<string>("bingeGroup");
        var richName = !string.IsNullOrEmpty(streamDesc)
            ? $"{streamName}\n{streamDesc}"
            : streamName;

        var info = new MediaSourceInfo
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            ETag = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Protocol = MediaProtocol.Http,
            MediaStreams = GetMediaStreamsWithExternalSubs(item),
            MediaAttachments = _inner.GetMediaAttachments(item.Id),
            Name = richName,
            Path = item.Path,
            RunTimeTicks = item.RunTimeTicks,
            Container = item.Container,
            Size = item.Size,
            Type = type,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            // just always say yes
            HasSegments = true,
            //HasSegments = MediaSegmentManager.HasSegments(item.Id)
        };


        if (user is not null)
        {
            info.SupportsTranscoding = user.HasPermission(
                PermissionKind.EnableVideoPlaybackTranscoding
            );
            info.SupportsDirectStream = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
        }
        if (string.IsNullOrEmpty(info.Path))
        {
            info.Type = MediaSourceType.Placeholder;
        }

        if (item is Video video)
        {
            info.IsoType = video.IsoType;
            info.VideoType = video.VideoType;
            info.Video3DFormat = video.Video3DFormat;
            info.Timestamp = video.Timestamp;
            info.IsRemote = true;

            if (video.IsShortcut)
            {
                info.IsRemote = true;
                info.Path = video.ShortcutPath;
            }
        }

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    private static readonly HashSet<string> _subtitleExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "vtt",
        "srt",
        "ass",
        "ssa",
        "sub",
        "idx",
        "smi",
    };

    // Jellyfin's MediaInfoResolver.GetExternalStreamsAsync bails immediately when !video.IsFileProtocol
    // (stream items have http:// paths). This means external subtitle files saved to the internal
    // metadata folder are never discovered during library refresh and never written to the DB.
    // We work around this by scanning the metadata folder ourselves at playback time and merging
    // any matching subtitle files into the DB streams on the fly.
    private IReadOnlyList<MediaStream> GetMediaStreamsWithExternalSubs(BaseItem item)
    {
        var streams = _inner.GetMediaStreams(item.Id).ToList();

        var gelatoFilename = item.GelatoData<string>("filename");
        if (string.IsNullOrEmpty(gelatoFilename))
            return streams;

        var metaPath = item.GetInternalMetadataPath();
        if (!Directory.Exists(metaPath))
            return streams;

        var baseName = Path.GetFileNameWithoutExtension(gelatoFilename);
        var existingPaths = new HashSet<string>(
            streams.Where(s => s.Path != null).Select(s => s.Path!),
            StringComparer.OrdinalIgnoreCase
        );

        var nextIndex = streams.Count > 0 ? streams.Max(s => s.Index) + 1 : 0;

        foreach (var file in Directory.EnumerateFiles(metaPath))
        {
            var fname = Path.GetFileName(file);

            // Must start with baseName + "."
            if (!fname.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            var ext = Path.GetExtension(fname).TrimStart('.');
            if (!_subtitleExtensions.Contains(ext))
                continue;

            if (existingPaths.Contains(file))
                continue;

            // Parse language from suffix: {baseName}.{lang}.{ext} or {baseName}.{lang}.{N}.{ext}
            var suffix = fname.Substring(baseName.Length + 1); // everything after "baseName."
            var parts = Path.GetFileNameWithoutExtension(suffix).Split('.');
            var langCode = parts.Length > 0 ? parts[0] : "und";

            streams.Add(
                new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    IsExternal = true,
                    IsExternalUrl = false,
                    SupportsExternalStream = true,
                    Path = file,
                    Language = langCode,
                    Codec = ext.ToLowerInvariant(),
                    Index = nextIndex++,
                    IsDefault = false,
                    IsForced = false,
                    IsHearingImpaired = false,
                }
            );

            existingPaths.Add(file);
        }

        return streams;
    }

    private async Task ProbeStreamAsync(Video owner, string streamUrl, CancellationToken ct)
    {
        var gelatoFilename = owner.GelatoData<string>("filename");
        var strmBaseName = !string.IsNullOrEmpty(gelatoFilename)
            ? Path.GetFileNameWithoutExtension(gelatoFilename)
            : $"{owner.Id:N}";
        var tmpStrm = Path.Combine(Path.GetTempPath(), $"{strmBaseName}.strm");
        await File.WriteAllTextAsync(tmpStrm, streamUrl, ct).ConfigureAwait(false);

        var origPath = owner.Path;
        var origShortcut = owner.IsShortcut;
        owner.Path = tmpStrm;
        owner.IsShortcut = true;
        owner.DateModified = new FileInfo(tmpStrm).LastWriteTimeUtc;

        try
        {
            _log.LogInformation("Probing stream for {Id} via {Url}", owner.Id, streamUrl);
            await owner.RefreshMetadata(
                new MetadataRefreshOptions(directoryService)
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                },
                ct
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stream probe failed for {Id}", owner.Id);
        }
        finally
        {
            owner.Path = origPath;
            owner.IsShortcut = origShortcut;
            try
            {
                File.Delete(tmpStrm);
            }
            catch
            { /* best effort */
            }
        }
    }
}
