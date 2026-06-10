using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

public sealed class DownloadFilter(
    ILibraryManager library,
    GelatoManager manager,
    IUserManager userManager,
    IMediaSourceManager mediaSourceManager,
    IHttpClientFactory httpClientFactory
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() != "GetDownload" || !ctx.TryGetRouteGuid(out var guid))
        {
            await next();
            return;
        }

        var userIdStr = ctx
            .HttpContext.User.Claims.FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")
            ?.Value;

        User? user = null;
        if (Guid.TryParse(userIdStr, out var userId))
        {
            user = userManager.GetUserById(userId);
        }

        if (user is not null)
        {
            var mediaSourceIdStr = ctx.HttpContext.Items["MediaSourceId"] as string;
            var hasMediaSourceId = Guid.TryParse(mediaSourceIdStr, out var mediaSourceId);

            var item = library.GetItemById<Video>(hasMediaSourceId ? mediaSourceId : guid, user);

            if (item != null && manager.IsStremio(item))
            {
                var path = item.Path;

                // some clients do not send mediasource id. the use the itemid in the query
                if (!hasMediaSourceId || !item.IsStream())
                {
                    var sources = mediaSourceManager.GetStaticMediaSources(item, true, user);
                    if (sources.Count == 0)
                    {
                        await next();
                        return;
                    }
                    path = sources[0].Path;
                }

                // Placeholder sources carry a null path (no streams synced yet);
                // passing that to HttpClient throws instead of falling through.
                if (string.IsNullOrEmpty(path))
                {
                    await next();
                    return;
                }

                var ct = ctx.HttpContext.RequestAborted;
                var client = httpClientFactory.CreateClient();

                var resp = await client.GetAsync(
                    path,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct
                );

                ctx.HttpContext.Response.RegisterForDispose(resp);

                resp.EnsureSuccessStatusCode();

                var stream = await resp.Content.ReadAsStreamAsync(ct);

                var contentType =
                    resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

                var fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    var uri = new Uri(path);
                    fileName = Path.GetFileName(uri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = "download";
                }

                if (resp.Content.Headers.ContentLength is { } len)
                {
                    ctx.HttpContext.Response.ContentLength = len;
                }

                ctx.Result = new FileStreamResult(stream, contentType)
                {
                    FileDownloadName = fileName,
                    EnableRangeProcessing = true,
                };
                return;
            }
        }

        await next();
    }
}
