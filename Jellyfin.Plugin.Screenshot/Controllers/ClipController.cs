using Jellyfin.Plugin.Screenshot.Model;
using Jellyfin.Plugin.Screenshot.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Screenshot.Controllers;

/// <summary>Authenticated clip preparation, streaming and download endpoints.</summary>
[ApiController]
[Authorize]
[Route("Screenshot/clips")]
public sealed class ClipController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IMediaSourceManager _sources;
    private readonly ClipService _clips;
    private readonly ILogger<ClipController> _logger;

    /// <summary>Initializes the clip controller.</summary>
    public ClipController(ILibraryManager library, IUserManager users, IMediaSourceManager sources, ClipService clips, ILogger<ClipController> logger)
    {
        _library = library;
        _users = users;
        _sources = sources;
        _clips = clips;
        _logger = logger;
    }

    private Guid UserId => MediaCaptureAccess.UserId(User);

    private Video? AccessibleVideo(Guid itemId)
    {
        var userId = UserId;
        if (userId == Guid.Empty || itemId == Guid.Empty) return null;
        var user = _users.GetUserById(userId);
        var item = _library.GetItemById<Video>(itemId);
        return MediaCaptureAccess.Allowed(item, user) ? item : null;
    }

    /// <summary>Renders a preview or a selected MP4 clip.</summary>
    [HttpPost]
    public async Task<ActionResult> Create([FromBody] ClipRequest request, CancellationToken cancellationToken)
    {
        var item = AccessibleVideo(request.ItemId);
        if (item is null) return NotFound("This video is unavailable or you do not have download permission.");
        var sources = _sources.GetStaticMediaSources(item, false, _users.GetUserById(UserId));
        var source = string.IsNullOrEmpty(request.MediaSourceId)
            ? sources.FirstOrDefault(s => s.Path == item.Path)
            : sources.FirstOrDefault(s => string.Equals(s.Id, request.MediaSourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null) return BadRequest("The selected media source is unavailable.");
        var sourceItem = MediaCaptureAccess.SourceVideo(_library, item, source, _users.GetUserById(UserId)!);
        if (sourceItem is null) return NotFound("This video is unavailable or you do not have download permission.");
        if (source.IsInfiniteStream || string.IsNullOrEmpty(source.Path) || !System.IO.File.Exists(source.Path))
            return BadRequest("Clipping requires a local video file with a known duration.");
        try
        {
            var range = ClipRange.FromRequest(request, source.RunTimeTicks ?? item.RunTimeTicks ?? 0);
            var clip = await _clips.CreateAsync(UserId, item, source, request, range, cancellationToken, sourceItem.Id).ConfigureAwait(false);
            if (AccessibleVideo(item.Id) is null || AccessibleVideo(sourceItem.Id) is null)
            {
                _clips.Remove(clip.Id, UserId);
                return NotFound("This video is unavailable or you do not have download permission.");
            }
            Response.Headers.CacheControl = "no-store";
            return Ok(new { clip.Id, clip.Filename, range.StartTicks, range.EndTicks, clip.Expires });
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (ClipBusyException)
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(429, "Clip rendering is busy. Close unused clip editors and try again shortly.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return StatusCode(499); }
        catch (OperationCanceledException) { return StatusCode(504, "Clip preparation timed out. Try a shorter selection."); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create clip for item {ItemId}", item.Id);
            return StatusCode(500, "The clip could not be created. Check the Jellyfin server log for details.");
        }
    }

    /// <summary>Streams a prepared clip with range support, or downloads it as an attachment.</summary>
    [HttpGet("{id:guid}")]
    public ActionResult Get(Guid id, [FromQuery] bool download = false)
    {
        var clip = _clips.Find(id, UserId);
        if (clip is null || AccessibleVideo(clip.ItemId) is null || AccessibleVideo(clip.SourceItemId) is null) return NotFound("The clip has expired or is unavailable.");
        try
        {
            var stream = new FileStream(clip.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Response.Headers.CacheControl = "private, no-store";
            return new FileStreamResult(stream, clip.ContentType)
            {
                EnableRangeProcessing = true,
                FileDownloadName = download ? clip.Filename : null
            };
        }
        catch (FileNotFoundException) { return NotFound("The clip has expired."); }
    }

    /// <summary>Returns the complete preview filmstrip after rechecking media access.</summary>
    [HttpGet("{id:guid}/filmstrip")]
    public ActionResult Filmstrip(Guid id)
    {
        var clip = _clips.Find(id, UserId);
        if (clip is null || !clip.Preview || AccessibleVideo(clip.ItemId) is null || AccessibleVideo(clip.SourceItemId) is null)
            return NotFound("The preview has expired or is unavailable.");
        try
        {
            var stream = new FileStream(clip.FilmstripPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Response.Headers.CacheControl = "private, no-store";
            return new FileStreamResult(stream, "image/jpeg");
        }
        catch (FileNotFoundException) { return NotFound("The preview has expired."); }
    }

    /// <summary>Releases a prepared preview or download belonging to this user.</summary>
    [HttpDelete("{id:guid}")]
    public ActionResult Delete(Guid id)
    {
        _clips.Remove(id, UserId);
        return NoContent();
    }
}
