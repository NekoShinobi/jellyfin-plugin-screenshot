using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<ScreenshotController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenshotController"/> class.
    /// </summary>
    public ScreenshotController(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        ILogger<ScreenshotController> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
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
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("capture")]
    [Authorize]
    public async Task<ActionResult> CaptureScreenshot(
        [FromQuery] Guid itemId,
        [FromQuery] long positionTicks,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Screenshot request — itemId={ItemId} positionTicks={Ticks}",
            itemId, positionTicks);

        if (itemId == Guid.Empty)
        {
            _logger.LogWarning("Received empty itemId");
            return BadRequest("itemId is required.");
        }

        var item = _libraryManager.GetItemById<Video>(itemId);
        if (item is null)
        {
            _logger.LogWarning("Item not found: {Id}", itemId);
            return NotFound("Item not found.");
        }

        _logger.LogInformation(
            "Item resolved — Name={Name} Path={Path} Container={Container}",
            item.Name, item.Path, item.Container);

        if (string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
        {
            _logger.LogError("Media file not accessible at path: {Path}", item.Path);
            return StatusCode(500, "Media file not accessible.");
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

        _logger.LogInformation(
            "Extracting frame — offset={Offset} outputPath={Output}",
            offset, outputPath);

        try
        {
            await ExtractFrameAccurate(ffmpegPath, item.Path, offset, outputPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Frame extraction failed for item {Name}", item.Name);
            return StatusCode(500, $"Frame extraction failed: {ex.Message}");
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
        CancellationToken cancellationToken)
    {
        // Pre-input fast seek to 10 s before target, then post-input accurate seek
        // for the remaining delta — efficient and frame-accurate.
        var preSeek = TimeSpan.FromSeconds(Math.Max(0, offset.TotalSeconds - 10));
        var postSeek = offset - preSeek;

        var args = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "-ss {0:F3} -i \"{1}\" -ss {2:F3} -vframes 1 -q:v 2 -y \"{3}\"",
            preSeek.TotalSeconds,
            inputPath,
            postSeek.TotalSeconds,
            outputPath);

        _logger.LogInformation("FFmpeg command: {Exe} {Args}", ffmpegPath, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

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

    private static string SanitizeFilename(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}
