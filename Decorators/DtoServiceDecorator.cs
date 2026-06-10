using Jellyfin.Data.Enums;
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
        var first = items.FirstOrDefault();

        if (first != null && first.GetBaseItemKind() == BaseItemKind.BoxSet)
        {
            options.EnableUserData = false;
        }

        var list = inner.GetBaseItemDtos(items, options, user, owner);

        // No stream prefetch on list/browse responses: streams are synced only when an
        // item's detail page is opened or playback starts (see InsertActionFilter /
        // MediaSourceManagerDecorator). Browsing a library, home screen, show or season
        // just returns the already-stored item metadata.

        // Inner returns one DTO per input item in order; pair them up so per-item
        // checks (CanDelete) run against the right item, not items[0].
        for (var i = 0; i < list.Count; i++)
        {
            Patch(list[i], i < items.Count ? items[i] : null, true, user);
        }
        return list;
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
            dto.CanDownload = true;
            // mark if placeholder
            if (
                isList
                || dto.MediaSources?.Length != 1
                || dto.Path is null
                || !(
                    dto.MediaSources[0].Path?.StartsWith(
                        "gelato",
                        StringComparison.OrdinalIgnoreCase
                    ) ?? false
                )
            )
            {
                return;
            }

            dto.LocationType = LocationType.Virtual;
            dto.Path = null;
            dto.CanDownload = false;
        }
    }
}
