using System.Diagnostics;
using Jellyfin.Plugin.Screenshot.Model;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Screenshot.Services;

internal sealed record StoredClip(Guid Id, Guid UserId, Guid ItemId, string Path, string Filename, ClipRange Range, bool Preview, DateTime Expires);
internal sealed class ClipBusyException : Exception { }

/// <summary>Bounded rendering and temporary storage for previews and downloadable clips.</summary>
public sealed class ClipService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StoredClip> _clips = new();
    private readonly HashSet<Guid> _active = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IMediaEncoder _encoder;
    private readonly ISubtitleEncoder _subtitles;
    private readonly ILogger<ClipService> _logger;
    private readonly string _directory;
    private readonly Timer _timer;

    /// <summary>Initializes clip rendering and expiry cleanup.</summary>
    public ClipService(IApplicationPaths paths, IMediaEncoder encoder, ISubtitleEncoder subtitles, ILogger<ClipService> logger)
    {
        _encoder = encoder;
        _subtitles = subtitles;
        _logger = logger;
        _directory = System.IO.Path.Combine(paths.CachePath, "screenshot-clips");
        Directory.CreateDirectory(_directory);
        _timer = new Timer(_ => Cleanup(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    internal async Task<StoredClip> CreateAsync(Guid userId, Video item, MediaSourceInfo source, ClipRequest request, ClipRange range, CancellationToken token)
    {
        lock (_gate)
        {
            while (_clips.Values.Count(c => c.UserId == userId) >= 3)
            {
                var oldest = _clips.Values.Where(c => c.UserId == userId && !c.Preview).OrderBy(c => c.Expires).FirstOrDefault();
                if (oldest is null) break;
                Remove(oldest.Id, userId);
            }
            // Include active requests in the quota to bound simultaneous completions.
            if (_active.Count >= 2 || _active.Contains(userId) || _clips.Count + _active.Count >= 8
                || _clips.Values.Count(c => c.UserId == userId) >= 3) throw new ClipBusyException();
            _active.Add(userId);
        }
        var id = Guid.NewGuid();
        var output = System.IO.Path.Combine(_directory, $"{id:N}.mp4");
        var assFile = System.IO.Path.Combine(_directory, $"{id:N}.ass");
        var retained = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var elapsed = Stopwatch.StartNew();
        _logger.LogInformation("Preparing {ClipKind} {ClipId} for item {ItemId}, duration {DurationSeconds}s", request.Preview ? "preview" : "clip", id, item.Id, range.DurationTicks / (double)TimeSpan.TicksPerSecond);
        try
        {
            var video = source.VideoStream ?? throw new ArgumentException("No video stream is available.");
            var audioIndex = request.AudioStreamIndex ?? source.DefaultAudioStreamIndex;
            if (audioIndex < -1) throw new ArgumentException("The selected audio track is invalid.");
            var audio = audioIndex == -1 ? null : source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && (audioIndex is null || s.Index == audioIndex));
            if (audioIndex is >= 0 && audio is null) throw new ArgumentException("The selected audio track is unavailable.");
            if (audio?.IsExternal == true) throw new ArgumentException("External audio tracks are not supported for clipping.");
            string? textSubtitle = null;
            string? bitmapSubtitle = null;
            if (request.SubtitleStreamIndex.HasValue)
            {
                var subtitle = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == request.SubtitleStreamIndex);
                if (subtitle is null) throw new ArgumentException("The selected subtitle track is unavailable.");
                if (subtitle.IsTextSubtitleStream)
                {
                    await using (var input = await _subtitles.GetSubtitles(item, source.Id, subtitle.Index, "ass", 0, 0, true, timeout.Token).ConfigureAwait(false))
                    await using (var file = new FileStream(assFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await input.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
                    }
                    textSubtitle = _encoder.EscapeSubtitleFilterPath(assFile);
                }
                else bitmapSubtitle = await _subtitles.GetSubtitleFilePath(subtitle, source, timeout.Token).ConfigureAwait(false);
            }
            var hdr = string.Equals(video.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
                || string.Equals(video.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
            var info = ClipEncoder.Build(_encoder.EncoderPath, source.Path, output, range,
                ClipEncoder.InputIndex(source, video), audio is null ? null : ClipEncoder.InputIndex(source, audio), request.Preview, textSubtitle, bitmapSubtitle, hdr);
            _logger.LogInformation("Starting FFmpeg for clip {ClipId}: {Width}x{Height}, HDR={Hdr}, subtitles={Subtitles}", id, video.Width, video.Height, hdr, request.SubtitleStreamIndex.HasValue);
            await ClipEncoder.RunAsync(info, timeout.Token).ConfigureAwait(false);
            if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new InvalidOperationException("FFmpeg produced no clip.");
            timeout.Token.ThrowIfCancellationRequested();
            var safeName = string.Concat(item.Name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (safeName.Length > 100) safeName = safeName[..100];
            if (safeName.Length == 0) safeName = "Clip";
            var filename = $"{safeName}-{TimeSpan.FromTicks(range.StartTicks).ToString(@"hh\-mm\-ss")}-{TimeSpan.FromTicks(range.EndTicks).ToString(@"hh\-mm\-ss")}.mp4";
            var clip = new StoredClip(id, userId, item.Id, output, filename, range, request.Preview, DateTime.UtcNow.AddMinutes(30));
            lock (_gate)
            {
                timeout.Token.ThrowIfCancellationRequested();
                _clips.Add(id, clip);
            }
            retained = true;
            _logger.LogInformation("Clip {ClipId} ready after {ElapsedSeconds:F1}s, {Bytes} bytes", id, elapsed.Elapsed.TotalSeconds, new FileInfo(output).Length);
            return clip;
        }
        finally
        {
            if (!retained) _logger.LogInformation("Clip {ClipId} preparation ended without output after {ElapsedSeconds:F1}s", id, elapsed.Elapsed.TotalSeconds);
            DeleteFile(assFile);
            if (!retained) DeleteFile(output);
            lock (_gate) _active.Remove(userId);
        }
    }

    internal StoredClip? Find(Guid id, Guid userId)
    {
        lock (_gate) return _clips.TryGetValue(id, out var clip) && clip.UserId == userId && clip.Expires > DateTime.UtcNow ? clip : null;
    }

    internal void Remove(Guid id, Guid userId)
    {
        lock (_gate)
        {
            if (_clips.TryGetValue(id, out var clip) && clip.UserId == userId)
            {
                DeleteFile(clip.Path);
                _clips.Remove(id);
            }
        }
    }

    private void Cleanup()
    {
        try
        {
            lock (_gate)
            {
                foreach (var clip in _clips.Values.Where(c => c.Expires <= DateTime.UtcNow).ToArray()) Remove(clip.Id, clip.UserId);
            }
            // Recover temporary files left by a server crash. Active renders time out in 10 minutes.
            foreach (var file in Directory.EnumerateFiles(_directory))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) DeleteFile(file);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not clean up expired clips"); }
    }

    private void DeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException ex) { _logger.LogWarning(ex, "Could not remove temporary clip"); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _timer.Dispose();
        lock (_gate) foreach (var clip in _clips.Values.ToArray()) Remove(clip.Id, clip.UserId);
        // Active operations retain the shutdown source until their linked tokens finish.
    }
}
