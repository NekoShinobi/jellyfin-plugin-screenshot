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

    internal static ProcessStartInfo Build(
        string encoder, string input, string output, ClipRange range,
        int videoIndex, int? audioIndex, bool preview,
        string? escapedTextSubtitle, string? bitmapSubtitle, bool hdr)
    {
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
        if (hdr)
        {
            // Produce a browser-compatible SDR MP4 from PQ/HLG sources.
            filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        }
        if (escapedTextSubtitle is not null) filters.Add($"subtitles=f='{escapedTextSubtitle}'");
        // Preserve source timestamps for subtitle rendering, then rebase both A/V streams
        // by the same offset so audio delay remains intact.
        filters.Add($"setpts=PTS-{Seconds(range.StartTicks)}/TB");
        filters.Add(preview
            ? "scale=w='min(960,iw)':h='min(540,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2");
        var video = $"0:{videoIndex}";
        if (bitmapSubtitle is not null)
        {
            Add("-filter_complex", $"[{video}][1:s:0]overlay=eof_action=pass:repeatlast=0,{string.Join(',', filters)}[clip]", "-map", "[clip]");
        }
        else Add("-map", video, "-vf", string.Join(',', filters));
        if (audioIndex.HasValue)
        {
            Add("-map", $"0:{audioIndex.Value}", "-af", $"asetpts=PTS-{Seconds(range.StartTicks)}/TB",
                "-c:a", "aac", "-b:a", "192k", "-ac", "2");
        }
        else Add("-an");
        Add("-t", Seconds(range.DurationTicks), "-c:v", "libx264", "-threads", "2", "-preset", "veryfast",
            "-crf", preview ? "27" : "20", "-maxrate", preview ? "2M" : "20M", "-bufsize", preview ? "4M" : "40M",
            "-pix_fmt", "yuv420p", "-sn", "-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart", "-y", output);
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
