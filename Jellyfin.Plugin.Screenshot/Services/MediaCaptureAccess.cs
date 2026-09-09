using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Library;

namespace Jellyfin.Plugin.Screenshot.Services;

internal static class MediaCaptureAccess
{
    internal static Guid UserId(ClaimsPrincipal principal) => Guid.TryParse(
        principal.Claims.FirstOrDefault(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value,
        out var id) ? id : Guid.Empty;

    internal static bool Allowed(Video? item, User? user) => user is not null && item is not null
        && !user.HasPermission(PermissionKind.IsDisabled)
        && item.GetPlayAccess(user) == PlayAccess.Full
        && item.IsVisibleStandalone(user) && item.IsAuthorizedToDownload(user);

    internal static Video? SourceVideo(ILibraryManager library, Video item, MediaSourceInfo source, User user)
    {
        // A media source list may include versions stored in another library.
        // Resolve that version independently rather than trusting list membership.
        var sourceItem = string.Equals(source.Path, item.Path, StringComparison.Ordinal) ? item
            : Guid.TryParse(source.Id, out var id) ? library.GetItemById<Video>(id) : null;
        return Allowed(sourceItem, user) && string.Equals(sourceItem!.Path, source.Path, StringComparison.Ordinal)
            ? sourceItem : null;
    }
}
