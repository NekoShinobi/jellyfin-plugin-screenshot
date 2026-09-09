using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.Screenshot.Model;

namespace Jellyfin.Plugin.Screenshot.Services;

internal static class ClipEncoder
{
    // Jellyfin stream indices include external files; FFmpeg input indices do not.
    internal static int InputIndex(MediaSourceInfo source, MediaStream stream)
    {
        var index = 0;
        foreach (var candidate in source.MediaStreams)
        {
            if (candidate == stream) return index;
            if (string.Equals(candidate.Path, stream.Path, StringComparison.Ordinal)) index++;
        }
        throw new ArgumentException("The selected stream is unavailable.");
    }

    // Reuse the small, tone-mapped/subtitled preview; pad short inputs to eight tiles.
    internal static ProcessStartInfo BuildFilmstrip(string encoder, string input, string output, long durationTicks)
    {
        var duration = ((decimal)durationTicks / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture);
        var info = new ProcessStartInfo(encoder)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        string[] args = ["-hide_banner", "-nostdin", "-loglevel", "error", "-threads", "2", "-filter_threads", "2",
            "-i", input, "-an", "-vf",
            $"setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={duration},fps=8/{duration},scale=w='max(160,90*dar)':h='max(90,160/dar)',setsar=1,crop=160:90,tile=8x1:nb_frames=8",
            "-frames:v", "1", "-c:v", "mjpeg", "-threads", "2", "-q:v", "3", "-update", "1", "-y", output];
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    internal static ProcessStartInfo Build(
        string encoder, string input, string output, ClipRange range,
        int videoIndex, int? audioIndex, bool preview,
        string? escapedTextSubtitle, string? bitmapSubtitle, bool hdr, bool webmPreview = false)
    {
        var webm = preview && webmPreview;
        var info = new ProcessStartInfo(encoder)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        void Add(params string[] args) { foreach (var arg in args) info.ArgumentList.Add(arg); }
        string Seconds(long ticks) => ((decimal)ticks / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture);
        Add("-hide_banner", "-nostdin", "-loglevel", "error", "-threads", "2", "-filter_threads", "2", "-filter_complex_threads", "2",
            "-copyts", "-ss", Seconds(range.StartTicks), "-i", input);
        if (bitmapSubtitle is not null) Add("-i", bitmapSubtitle);

        var filters = new List<string>();
        if (preview)
        {
            // Drop excess frames and shrink before expensive HDR/subtitle processing.
            // Unknown input frame rates fall back to 24; low-frame-rate sources stay low.
            filters.Add("fps=fps='if(gt(source_fps,0),min(source_fps,24),24)'");
            filters.Add("scale=w='min(960,iw)':h='min(540,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2");
        }
        if (hdr)
        {
            // Produce browser-compatible SDR from PQ/HLG sources.
            filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        }
        if (escapedTextSubtitle is not null) filters.Add($"subtitles=f='{escapedTextSubtitle}'");
        // Preserve source timestamps for subtitle rendering, then rebase both A/V streams
        // by the same offset so audio delay remains intact.
        filters.Add($"setpts=PTS-{Seconds(range.StartTicks)}/TB");
        if (!preview) filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        var video = $"0:{videoIndex}";
        if (bitmapSubtitle is not null)
        {
            Add("-filter_complex", $"[{video}][1:s:0]overlay=eof_action=pass:repeatlast=0,{string.Join(',', filters)}[clip]", "-map", "[clip]");
        }
        else Add("-map", video, "-vf", string.Join(',', filters));
        if (audioIndex.HasValue)
        {
            Add("-map", $"0:{audioIndex.Value}", "-af", $"asetpts=PTS-{Seconds(range.StartTicks)}/TB",
                "-c:a", webm ? "libopus" : "aac", "-b:a", webm ? "96k" : "192k", "-ac", "2");
        }
        else Add("-an");
        if (preview) Add("-g", "24");
        Add("-t", Seconds(range.DurationTicks), "-threads", "2");
        if (webm)
        {
            // CEF builds without H.264/AAC can decode VP8/Opus in HTML video.
            Add("-c:v", "libvpx", "-deadline", "realtime", "-cpu-used", "8", "-lag-in-frames", "0",
                "-b:v", "1500k", "-crf", "10", "-maxrate", "2M", "-bufsize", "4M");
        }
        else
        {
            Add("-c:v", "libx264", "-preset", preview ? "ultrafast" : "veryfast",
                "-crf", preview ? "27" : "20", "-maxrate", preview ? "2M" : "20M", "-bufsize", preview ? "4M" : "40M",
                "-movflags", "+faststart");
        }
        Add("-pix_fmt", "yuv420p", "-sn", "-map_metadata", "-1", "-map_chapters", "-1", "-y", output);
        return info;
    }

    internal static async Task RunAsync(ProcessStartInfo info, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = info };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        // Drain stderr while encoding; retain only a bounded tail for diagnostics.
        var stderr = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            throw;
        }
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}: {error}");
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var tail = string.Empty;
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            tail += new string(buffer, 0, count);
            if (tail.Length > 8192) tail = tail[^8192..];
        }
        return tail;
    }
}
