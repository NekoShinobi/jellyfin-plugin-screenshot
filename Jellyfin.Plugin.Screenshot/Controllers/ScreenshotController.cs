using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
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
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly ILogger<ScreenshotController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenshotController"/> class.
    /// </summary>
    public ScreenshotController(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        ISubtitleEncoder subtitleEncoder,
        ILogger<ScreenshotController> logger)
    {
        _libraryManager = libraryManager;
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
        var resourceName = $"{GetType().Namespace!.Replace(".Controllers", string.Empty)}.js.screenshot.js";
        _logger.LogDebug("Serving script resource: {Name}", resourceName);

        var stream = GetType().Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogError("Embedded resource not found: {Name}", resourceName);
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache, must-revalidate";
        return new FileStreamResult(stream, "application/javascript");
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

        var item = _libraryManager.GetItemById<Video>(itemId);
        if (item is null)
        {
            _logger.LogWarning("Item not found: {Id}", itemId);
            return NotFound("Item not found.");
        }

        var mediaSources = _mediaSourceManager.GetStaticMediaSources(item, false);
        var mediaSource = FindMediaSource(mediaSources, mediaSourceId, item.Path);
        var inputPath = mediaSource?.Path ?? item.Path;

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
        string? subtitlePath = null;
        var isTextSubtitle = false;

        _logger.LogInformation(
            "Extracting frame — offset={Offset} outputPath={Output}",
            offset, outputPath);

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
                subtitlePath = isTextSubtitle
                    ? await CreateShiftedAssFile(item, mediaSource, subtitleStreamIndex.Value, offset, cancellationToken)
                        .ConfigureAwait(false)
                    : await _subtitleEncoder.GetSubtitleFilePath(subtitleStream, mediaSource, cancellationToken)
                        .ConfigureAwait(false);
            }

            await ExtractFrameAccurate(
                    ffmpegPath,
                    inputPath,
                    offset,
                    outputPath,
                    subtitlePath,
                    isTextSubtitle,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Frame extraction failed for item {Name}", item.Name);
            return StatusCode(500, $"Frame extraction failed: {ex.Message}");
        }
        finally
        {
            DeleteTempFile(subtitlePath, isTextSubtitle);
        }

        if (!System.IO.File.Exists(outputPath))
        {
            _logger.LogError("Output file missing after FFmpeg completed: {Path}", outputPath);
            return StatusCode(500, "Extracted image not found after FFmpeg run.");
        }

        var fileInfo = new FileInfo(outputPath);
        _logger.LogInformation("Output file created — size={Bytes} bytes", fileInfo.Length);

        if (fileInfo.Length == 0)
        {
            _logger.LogError("Output file is empty: {Path}", outputPath);
            System.IO.File.Delete(outputPath);
            return StatusCode(500, "Extracted image is empty.");
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(outputPath, cancellationToken)
            .ConfigureAwait(false);

        try { System.IO.File.Delete(outputPath); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete temp file: {Path}", outputPath); }

        var filename = $"{SanitizeFilename(item.Name)}_{offset:hh\\-mm\\-ss}.jpg";
        _logger.LogInformation("Returning {Bytes} bytes as '{Filename}'", bytes.Length, filename);

        return File(bytes, "image/jpeg", filename);
    }

    /// <summary>
    /// Spawns FFmpeg with a two-stage seek for frame-accurate extraction.
    /// Stderr is read concurrently with WaitForExitAsync to prevent pipe buffer deadlock
    /// (FFmpeg is verbose; blocking on a full stderr pipe would hang indefinitely).
    /// </summary>
    private async Task ExtractFrameAccurate(
        string ffmpegPath,
        string inputPath,
        TimeSpan offset,
        string outputPath,
        string? subtitlePath,
        bool isTextSubtitle,
        CancellationToken cancellationToken)
    {
        // Pre-input fast seek to 10 s before target, then post-input accurate seek
        // for the remaining delta — efficient and frame-accurate.
        var preSeek = TimeSpan.FromSeconds(Math.Max(0, offset.TotalSeconds - 10));
        var postSeek = offset - preSeek;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        AddArgument(process.StartInfo, "-ss", FormatSeconds(preSeek));
        AddArgument(process.StartInfo, "-i", inputPath);

        if (!string.IsNullOrEmpty(subtitlePath) && !isTextSubtitle)
        {
            AddArgument(process.StartInfo, "-ss", FormatSeconds(preSeek));
            AddArgument(process.StartInfo, "-i", subtitlePath);
        }

        AddArgument(process.StartInfo, "-ss", FormatSeconds(postSeek));

        if (!string.IsNullOrEmpty(subtitlePath))
        {
            if (isTextSubtitle)
            {
                var escapedSubtitlePath = _mediaEncoder.EscapeSubtitleFilterPath(subtitlePath);
                AddArgument(process.StartInfo, "-vf", $"subtitles=f='{escapedSubtitlePath}'");
            }
            else
            {
                AddArgument(
                    process.StartInfo,
                    "-filter_complex",
                    "[0:v:0][1:s:0]overlay=eof_action=pass:repeatlast=0");
            }
        }

        AddArgument(process.StartInfo, "-frames:v", "1");
        AddArgument(process.StartInfo, "-q:v", "2");
        AddArgument(process.StartInfo, "-y", outputPath);

        _logger.LogInformation(
            "Starting FFmpeg frame extraction with subtitles={WithSubtitles}",
            !string.IsNullOrEmpty(subtitlePath));

        process.Start();

        // Read stderr concurrently — if we wait for exit first, FFmpeg's verbose
        // output fills the pipe buffer, FFmpeg blocks, and WaitForExitAsync deadlocks.
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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
    }

    private async Task<string> CreateShiftedAssFile(
        Video item,
        MediaSourceInfo mediaSource,
        int subtitleStreamIndex,
        TimeSpan offset,
        CancellationToken cancellationToken)
    {
        var preSeek = TimeSpan.FromSeconds(Math.Max(0, offset.TotalSeconds - 10));
        var endTime = offset + TimeSpan.FromSeconds(1);
        var path = Path.Combine(Path.GetTempPath(), $"sc_sub_{Guid.NewGuid():N}.ass");

        try
        {
            await using var subtitle = await _subtitleEncoder.GetSubtitles(
                    item,
                    mediaSource.Id,
                    subtitleStreamIndex,
                    "ass",
                    preSeek.Ticks,
                    endTime.Ticks,
                    false,
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
            var selectedSource = mediaSources.FirstOrDefault(source =>
                string.Equals(source.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
            if (selectedSource is not null)
            {
                return selectedSource;
            }
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
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}
