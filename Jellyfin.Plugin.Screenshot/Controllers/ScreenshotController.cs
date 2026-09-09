using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Jellyfin.Plugin.Screenshot.Services;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Screenshot.Controllers;

/// <summary>
/// API controller for screenshot capture operations.
/// </summary>
[ApiController]
[Route("Screenshot")]
public class ScreenshotController : ControllerBase
{
    /// <summary>
    /// Upper bound for the FFmpeg run. An accurate seek only decodes from the keyframe
    /// preceding the target, so a healthy extraction takes a couple of seconds at most;
    /// anything beyond this is a stuck process and is killed rather than left to run
    /// past the point where the client has given up waiting.
    /// </summary>
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(45);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly ILogger<ScreenshotController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenshotController"/> class.
    /// </summary>
    public ScreenshotController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        ISubtitleEncoder subtitleEncoder,
        ILogger<ScreenshotController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _subtitleEncoder = subtitleEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Serves the frontend JavaScript bundle.
    /// </summary>
    [HttpGet("script")]
    [AllowAnonymous]
    public ActionResult GetScript()
    {
        var assembly = GetType().Assembly;
        var prefix = typeof(Plugin).Namespace;
        var parts = new List<string>();
        foreach (var name in new[] { "screenshot.js", "clipping.js" })
        {
            using var stream = assembly.GetManifestResourceStream($"{prefix}.js.{name}");
            if (stream is null) return NotFound();
            using var reader = new StreamReader(stream);
            parts.Add(reader.ReadToEnd());
        }
        Response.Headers.CacheControl = "no-cache, must-revalidate";
        return Content(string.Join("\n;\n", parts), "application/javascript", System.Text.Encoding.UTF8);
    }

    /// <summary>Serves the isolated clip editor stylesheet.</summary>
    [HttpGet("clipping.css")]
    [AllowAnonymous]
    public ActionResult GetClipStyles()
    {
        var stream = GetType().Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.js.clipping.css");
        if (stream is null) return NotFound();
        Response.Headers.CacheControl = "no-cache, must-revalidate";
        return new FileStreamResult(stream, "text/css");
    }

    /// <summary>
    /// Captures a frame at the given playback position and returns it as a JPEG download.
    /// </summary>
    /// <param name="itemId">Jellyfin item ID of the video.</param>
    /// <param name="positionTicks">Playback position in ticks (1 tick = 100 ns).</param>
    /// <param name="mediaSourceId">Currently playing media source ID, when known.</param>
    /// <param name="subtitleStreamIndex">Subtitle stream to burn into the image, when requested.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("capture")]
    [Authorize]
    public async Task<ActionResult> CaptureScreenshot(
        [FromQuery] Guid itemId,
        [FromQuery] long positionTicks,
        [FromQuery] string? mediaSourceId,
        [FromQuery] int? subtitleStreamIndex,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Screenshot request — itemId={ItemId} positionTicks={Ticks} subtitleStreamIndex={SubtitleIndex}",
            itemId, positionTicks, subtitleStreamIndex);

        if (itemId == Guid.Empty)
        {
            _logger.LogWarning("Received empty itemId");
            return BadRequest("itemId is required.");
        }

        if (positionTicks < 0)
        {
            return BadRequest("positionTicks cannot be negative.");
        }

        Response.Headers.CacheControl = "private, no-store";
        var userId = MediaCaptureAccess.UserId(User);
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        var item = user is null ? null : _libraryManager.GetItemById<Video>(itemId);
        if (!MediaCaptureAccess.Allowed(item, user))
            return NotFound("This video is unavailable or you do not have download permission.");

        var mediaSources = _mediaSourceManager.GetStaticMediaSources(item!, false, user);
        var mediaSource = FindMediaSource(mediaSources, mediaSourceId, item!.Path);
        if (mediaSource is null) return BadRequest("The selected media source is unavailable.");
        var sourceItem = MediaCaptureAccess.SourceVideo(_libraryManager, item, mediaSource, user!);
        if (sourceItem is null) return NotFound("This video is unavailable or you do not have download permission.");
        var inputPath = mediaSource.Path;

        _logger.LogInformation(
            "Item resolved — Name={Name} Path={Path} Container={Container} MediaSourceId={MediaSourceId}",
            item.Name, inputPath, mediaSource?.Container ?? item.Container, mediaSource?.Id);

        if (string.IsNullOrEmpty(inputPath) || !System.IO.File.Exists(inputPath))
        {
            _logger.LogError("Media file not accessible at path: {Path}", inputPath);
            return StatusCode(500, "Media file not accessible.");
        }

        if (subtitleStreamIndex.HasValue && mediaSource is null)
        {
            _logger.LogWarning("Subtitle requested but no media source was found for item {Id}", itemId);
            return BadRequest("The selected media source could not be resolved.");
        }

        var ffmpegPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            _logger.LogError("FFmpeg path is empty — encoder not configured");
            return StatusCode(500, "FFmpeg not available.");
        }

        _logger.LogInformation("Using FFmpeg at: {Path}", ffmpegPath);

        var offset = TimeSpan.FromTicks(positionTicks);
        var outputPath = Path.Combine(Path.GetTempPath(), $"sc_{Guid.NewGuid():N}.jpg");
        var videoStreamIndex = FindVideoStreamIndex(mediaSource);
        string? subtitlePath = null;
        var isTextSubtitle = false;

        _logger.LogInformation(
            "Extracting frame — offset={Offset} outputPath={Output}",
            offset, outputPath);

        using var ffmpegCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ffmpegCts.CancelAfter(FfmpegTimeout);

        try
        {
            if (subtitleStreamIndex.HasValue)
            {
                var subtitleStream = mediaSource!.MediaStreams.FirstOrDefault(stream =>
                    stream.Type == MediaStreamType.Subtitle && stream.Index == subtitleStreamIndex.Value);

                if (subtitleStream is null)
                {
                    _logger.LogWarning(
                        "Subtitle stream {Index} was not found in media source {MediaSourceId}",
                        subtitleStreamIndex, mediaSource.Id);
                    return BadRequest("The selected subtitle stream could not be found.");
                }

                isTextSubtitle = subtitleStream.IsTextSubtitleStream;

                // Preparing an embedded subtitle makes Jellyfin extract the file's subtitle
                // tracks into its subtitle cache, which on the first request for a large file
                // can outlast the client's patience. Deliberately uncancellable: aborting the
                // extraction discards it, so every retry would start over and fail the same
                // way, whereas letting it finish fills the cache and makes the next capture
                // immediate. Jellyfin bounds this itself through the server's
                // SubtitleExtractionTimeoutMinutes setting.
                var subtitleTimer = Stopwatch.StartNew();

                subtitlePath = isTextSubtitle
                    ? await CreateAssFile(item, mediaSource, subtitleStreamIndex.Value, CancellationToken.None)
                        .ConfigureAwait(false)
                    : await _subtitleEncoder.GetSubtitleFilePath(subtitleStream, mediaSource, CancellationToken.None)
                        .ConfigureAwait(false);

                _logger.LogInformation(
                    "Subtitle prepared in {Elapsed} ms — isTextSubtitle={IsText} path={Path}",
                    subtitleTimer.ElapsedMilliseconds, isTextSubtitle, subtitlePath);
            }

            var ffmpegTimer = Stopwatch.StartNew();

            var stderr = await ExtractFrameAccurate(
                    ffmpegPath,
                    inputPath,
                    offset,
                    outputPath,
                    subtitlePath,
                    isTextSubtitle,
                    videoStreamIndex,
                    ffmpegCts.Token)
                .ConfigureAwait(false);

            _logger.LogInformation("FFmpeg finished in {Elapsed} ms", ffmpegTimer.ElapsedMilliseconds);

            if (!System.IO.File.Exists(outputPath))
            {
                // FFmpeg reports success when it reaches the end of the input without ever
                // producing a frame, so a missing file means the filter chain discarded
                // everything rather than that the write failed.
                _logger.LogError(
                    "FFmpeg exited successfully but wrote no image to {Path}. stderr: {Stderr}",
                    outputPath,
                    stderr);
                return StatusCode(500, "FFmpeg produced no image for that position.");
            }

            var fileInfo = new FileInfo(outputPath);
            _logger.LogInformation("Output file created — size={Bytes} bytes", fileInfo.Length);

            if (fileInfo.Length == 0)
            {
                _logger.LogError("Output file is empty: {Path}", outputPath);
                return StatusCode(500, "Extracted image is empty.");
            }

            var bytes = await System.IO.File.ReadAllBytesAsync(outputPath, cancellationToken)
                .ConfigureAwait(false);
            var filename = BuildFilename(item, offset);
            _logger.LogInformation("Returning {Bytes} bytes as '{Filename}'", bytes.Length, filename);

            // Access may have been revoked while extracting subtitles or the image.
            var currentUser = _userManager.GetUserById(userId);
            if (!MediaCaptureAccess.Allowed(_libraryManager.GetItemById<Video>(item.Id), currentUser)
                || !MediaCaptureAccess.Allowed(_libraryManager.GetItemById<Video>(sourceItem.Id), currentUser))
                return NotFound("This video is unavailable or you do not have download permission.");
            return File(bytes, "image/jpeg", filename);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Frame extraction canceled for item {Name} because the client disconnected", item.Name);
            return StatusCode(499);
        }
        catch (OperationCanceledException)
        {
            _logger.LogError(
                "FFmpeg was killed after exceeding the {Seconds}s frame extraction limit for item {Name}",
                FfmpegTimeout.TotalSeconds,
                item.Name);
            return StatusCode(504, $"Frame extraction timed out after {FfmpegTimeout.TotalSeconds:F0} seconds.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Frame extraction failed for item {Name}", item.Name);
            return StatusCode(500, "Frame extraction failed. Check the Jellyfin server log for details.");
        }
        finally
        {
            DeleteTempFile(subtitlePath, isTextSubtitle);
            DeleteTempFile(outputPath, true);
        }
    }

    /// <summary>
    /// Spawns FFmpeg to write a single frame, and returns its stderr output.
    /// Stderr is read concurrently with WaitForExitAsync to prevent pipe buffer deadlock
    /// (FFmpeg is verbose; blocking on a full stderr pipe would hang indefinitely).
    /// </summary>
    private async Task<string> ExtractFrameAccurate(
        string ffmpegPath,
        string inputPath,
        TimeSpan offset,
        string outputPath,
        string? subtitlePath,
        bool isTextSubtitle,
        int videoStreamIndex,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        var startInfo = process.StartInfo;

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        AddArgument(startInfo, "-loglevel", "warning");

        // Keep the source timestamps across the input seek. Without -copyts FFmpeg rebases
        // the seek point to zero, and then libass renders whatever is at the start of the
        // file instead of what is on screen — while any filter or output option that
        // compares against the real position discards every frame, leaving FFmpeg to decode
        // to the end of the file and exit successfully without ever writing an image.
        startInfo.ArgumentList.Add("-copyts");

        // A single input-side seek. Accurate seeking (FFmpeg's default) decodes from the
        // keyframe preceding the target and discards the rest, so the cost is one GOP
        // regardless of how far into the file the position is.
        AddArgument(startInfo, "-ss", FormatSeconds(offset));
        AddArgument(startInfo, "-i", inputPath);

        // Fall back to FFmpeg's own selection when the index is unknown; addressing the
        // stream directly avoids picking up embedded cover art as "the" video stream.
        var videoPad = videoStreamIndex >= 0
            ? $"0:{videoStreamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "0:v:0";
        var hasSubtitle = !string.IsNullOrEmpty(subtitlePath);

        if (hasSubtitle && !isTextSubtitle)
        {
            // Graphical subtitles come from the extracted stream, read from its start: an
            // event that is still on screen at the capture point was signalled earlier, and
            // seeking a raw subtitle stream would skip the packet that carries it.
            AddArgument(startInfo, "-i", subtitlePath!);
        }

        if (hasSubtitle && isTextSubtitle)
        {
            var escapedSubtitlePath = _mediaEncoder.EscapeSubtitleFilterPath(subtitlePath!);

            AddArgument(startInfo, "-map", videoPad);
            AddArgument(
                startInfo,
                "-vf",
                $"subtitles=f='{escapedSubtitlePath}',setpts=PTS-STARTPTS");
        }
        else if (hasSubtitle)
        {
            AddArgument(
                startInfo,
                "-filter_complex",
                $"[{videoPad}][1:s:0]overlay=eof_action=pass:repeatlast=0,setpts=PTS-STARTPTS");
        }
        else
        {
            AddArgument(startInfo, "-map", videoPad);
        }

        startInfo.ArgumentList.Add("-an");
        AddArgument(startInfo, "-frames:v", "1");
        AddArgument(startInfo, "-q:v", "2");

        // The output is a single file rather than a numbered sequence; without -update
        // the image2 muxer warns about the missing pattern on every capture.
        AddArgument(startInfo, "-update", "1");
        AddArgument(startInfo, "-y", outputPath);

        _logger.LogInformation(
            "Starting FFmpeg frame extraction with subtitles={WithSubtitles}: {Command} {Arguments}",
            hasSubtitle,
            ffmpegPath,
            string.Join(' ', startInfo.ArgumentList));

        process.Start();
        process.StandardInput.Close();

        // Read stderr concurrently — if we wait for exit first, FFmpeg's verbose
        // output fills the pipe buffer, FFmpeg blocks, and WaitForExitAsync deadlocks.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the HasExited check and Kill.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        _logger.LogDebug("FFmpeg exit code: {Code}", process.ExitCode);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogDebug("FFmpeg stderr: {Stderr}", stderr);
        }
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogDebug("FFmpeg stdout: {Stdout}", stdout);
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}. stderr: {stderr}");
        }

        return stderr;
    }

    /// <summary>
    /// Resolves the FFmpeg stream index of the media source's video stream.
    /// Mirrors <c>EncodingHelper.FindIndex</c>: a media source can span several files
    /// (external subtitles alongside the video), and only the streams that live in the
    /// same file count towards the index FFmpeg sees for that input.
    /// </summary>
    private static int FindVideoStreamIndex(MediaSourceInfo? mediaSource)
    {
        var streams = mediaSource?.MediaStreams;
        var videoStream = mediaSource?.VideoStream;

        if (streams is null || videoStream is null)
        {
            return -1;
        }

        var index = 0;
        foreach (var stream in streams)
        {
            if (stream == videoStream)
            {
                return index;
            }

            if (string.Equals(stream.Path, videoStream.Path, StringComparison.Ordinal))
            {
                index++;
            }
        }

        return -1;
    }

    private async Task<string> CreateAssFile(
        Video item,
        MediaSourceInfo mediaSource,
        int subtitleStreamIndex,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc_sub_{Guid.NewGuid():N}.ass");

        try
        {
            await using var subtitle = await _subtitleEncoder.GetSubtitles(
                    item,
                    mediaSource.Id,
                    subtitleStreamIndex,
                    "ass",
                    0,
                    0,
                    true,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await subtitle.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }

            throw;
        }

        return path;
    }

    private static MediaSourceInfo? FindMediaSource(
        IReadOnlyList<MediaSourceInfo> mediaSources,
        string? mediaSourceId,
        string? itemPath)
    {
        if (!string.IsNullOrEmpty(mediaSourceId))
        {
            return mediaSources.FirstOrDefault(source =>
                string.Equals(source.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
        }

        return mediaSources.FirstOrDefault(source =>
                   string.Equals(source.Path, itemPath, StringComparison.Ordinal))
            ?? mediaSources.FirstOrDefault();
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static string FormatSeconds(TimeSpan value)
        => value.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    private static string BuildFilename(Video item, TimeSpan offset)
    {
        var name = SanitizeFilename(item.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Screenshot";
        }

        var episodeCode = item is Episode
            && item.ParentIndexNumber.HasValue
            && item.IndexNumber.HasValue
                ? $"-S{item.ParentIndexNumber.Value:D2}E{item.IndexNumber.Value:D2}"
                : string.Empty;
        var totalHours = (long)Math.Floor(offset.TotalHours);
        var timestamp = $"{totalHours:D2}-{offset.Minutes:D2}-{offset.Seconds:D2}";

        return $"{name}{episodeCode}-{timestamp}.jpg";
    }

    private void DeleteTempFile(string? path, bool isTemporary)
    {
        if (!isTemporary || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete temporary subtitle file: {Path}", path);
        }
    }

    private static string SanitizeFilename(string name)
    {
        foreach (var c in "<>:\"/\\|?*")
        {
            name = name.Replace(c, '_');
        }

        foreach (var c in name.Where(c => char.IsControl(c)).Distinct())
        {
            name = name.Replace(c, '_');
        }

        return name.TrimEnd('.', ' ');
    }
}
