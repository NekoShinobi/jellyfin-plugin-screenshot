using System.Diagnostics;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using System.Text.Json;
using Jellyfin.Plugin.Screenshot.Model;
using Jellyfin.Plugin.Screenshot.Services;
using Jellyfin.Plugin.Screenshot.Controllers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Screenshot.Tests;

public sealed class ClipTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    [Theory]
    [InlineData(5, 60, 60, 100, 0, 65)]
    [InlineData(95, 60, 60, 100, 35, 100)]
    [InlineData(120, 60, 60, 240, 60, 180)]
    [InlineData(30, 30, 0, 240, 0, 30)]
    public void RangeClampsToMedia(double anchor, double before, double after, double runtime, double start, double end)
    {
        var range = ClipRange.Create((long)(anchor * Second), (long)(before * Second), (long)(after * Second), (long)(runtime * Second));
        Assert.Equal((long)(start * Second), range.StartTicks);
        Assert.Equal((long)(end * Second), range.EndTicks);
    }

    [Theory]
    [InlineData(-1, 1, 1, 100)]
    [InlineData(10, 61, 0, 100)]
    [InlineData(10, 0, 61, 100)]
    [InlineData(10, 0, 0, 100)]
    [InlineData(0, 1, 0, 100)]
    [InlineData(100, 0, 1, 100)]
    [InlineData(101, 1, 1, 100)]
    [InlineData(10, -1, 1, 100)]
    [InlineData(0, 1, 1, 0)]
    public void InvalidRangeIsRejected(long anchor, long before, long after, long runtime)
        => Assert.Throws<ArgumentException>(() => ClipRange.Create(anchor * Second, before * Second, after * Second, runtime * Second));

    [Fact]
    public void TickArithmeticCannotOverflow()
    {
        var range = ClipRange.Create(long.MaxValue - 2, 1, 60 * Second, long.MaxValue);
        Assert.Equal(long.MaxValue, range.EndTicks);
        Assert.Throws<ArgumentException>(() => ClipRange.Create(1, long.MaxValue, 0, 100));
    }

    [Fact]
    public void ExternalTracksDoNotShiftInputStreamIndices()
    {
        var video = new MediaStream { Type = MediaStreamType.Video, Index = 2 };
        var source = new MediaSourceInfo { MediaStreams = new[] {
            new MediaStream { Type = MediaStreamType.Subtitle, Path = "external.srt", IsExternal = true },
            new MediaStream { Type = MediaStreamType.Audio }, video } };
        Assert.Equal(1, ClipEncoder.InputIndex(source, video));
    }

    [Fact]
    public async Task RequestsWithoutAUserCannotRenderOrReadClips()
    {
        var root = Path.Combine(Path.GetTempPath(), "clip-access-" + Guid.NewGuid());
        var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.CachePath).Returns(root);
        using var service = new ClipService(paths.Object, Mock.Of<IMediaEncoder>(), Mock.Of<ISubtitleEncoder>(), NullLogger<ClipService>.Instance);
        var controller = new ClipController(Mock.Of<ILibraryManager>(), Mock.Of<IUserManager>(), Mock.Of<IMediaSourceManager>(), service, NullLogger<ClipController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<NotFoundObjectResult>(await controller.Create(new ClipRequest(), CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(controller.Get(Guid.NewGuid()));
        Assert.Null(service.Find(Guid.NewGuid(), Guid.NewGuid()));
        service.Dispose();
        Directory.Delete(root, true);
    }

    // Set SCREENSHOT_FFMPEG and SCREENSHOT_FFPROBE for real encoder integration tests.
    [FfmpegFact]
    public async Task RealClipHasAccurateDurationOriginalAspectAndAudio()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SCREENSHOT_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("SCREENSHOT_FFPROBE");
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe)) return;
        var root = Path.Combine(Path.GetTempPath(), "clip-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "source with spaces.mkv");
            await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=8", "-f", "lavfi", "-i", "sine=frequency=440:duration=8", "-c:v", "libx264", "-threads", "2", "-c:a", "aac", "-y", input);
            var output = Path.Combine(root, "clip.mp4");
            await ClipEncoder.RunAsync(ClipEncoder.Build(ffmpeg, input, output, new ClipRange(23 * Second / 10, 57 * Second / 10), 0, 1, false, null, null, false), CancellationToken.None);
            var probe = JsonDocument.Parse(await Run(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", output));
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
            Assert.Equal(320, video.GetProperty("width").GetInt32());
            Assert.Equal(240, video.GetProperty("height").GetInt32());
            Assert.Equal("4:3", video.GetProperty("display_aspect_ratio").GetString());
            Assert.Contains(streams, s => s.GetProperty("codec_type").GetString() == "audio");
            var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 3.35, 3.5);
            // Anamorphic source retains its display ratio in the smaller preview.
            var anamorphic = Path.Combine(root, "anamorphic.mkv");
            await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", input, "-vf", "setsar=2", "-c:v", "libx264", "-threads", "2", "-an", "-y", anamorphic);
            var preview = Path.Combine(root, "preview.mp4");
            await ClipEncoder.RunAsync(ClipEncoder.Build(ffmpeg, anamorphic, preview, new ClipRange(Second, 2 * Second), 0, null, true, null, null, false), CancellationToken.None);
            var previewProbe = JsonDocument.Parse(await Run(ffprobe, "-v", "error", "-show_streams", "-of", "json", preview));
            Assert.Equal("8:3", previewProbe.RootElement.GetProperty("streams")[0].GetProperty("display_aspect_ratio").GetString());
            // Subtitle timing follows source timestamps after a non-keyframe seek.
            var black = Path.Combine(root, "black.mkv");
            await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:size=320x240:rate=25:duration=8", "-c:v", "libx264", "-threads", "2", "-y", black);
            var subtitle = Path.Combine(root, "captions.srt");
            await File.WriteAllTextAsync(subtitle, "1\n00:00:02,200 --> 00:00:03,000\nVISIBLE AT CLIP START\n");
            var captioned = Path.Combine(root, "captioned.mp4");
            await ClipEncoder.RunAsync(ClipEncoder.Build(ffmpeg, black, captioned, new ClipRange(23 * Second / 10, 43 * Second / 10), 0, null, false, subtitle, null, false), CancellationToken.None);
            var stats = await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", captioned, "-vf", "signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-");
            var averages = System.Text.RegularExpressions.Regex.Matches(stats, @"lavfi.signalstats.YAVG=([0-9.]+)").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.True(averages[0] > 16.1, $"Subtitle should appear immediately; luma={averages[0]}, max={averages.Max()}.");
            Assert.InRange(averages[^1], 15.9, 16.1);

            // Exercise actual service ownership, streaming, cleanup and revoked access.
            var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.CachePath).Returns(root);
            var encoder = new Mock<IMediaEncoder>(); encoder.SetupGet(e => e.EncoderPath).Returns(ffmpeg);
            using var clips = new ClipService(paths.Object, encoder.Object, Mock.Of<ISubtitleEncoder>(), NullLogger<ClipService>.Instance);
            var owner = new User("owner", "auth", "reset");
            owner.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
            owner.Permissions.Add(new Permission(PermissionKind.EnableContentDownloading, true));
            var item = new Mock<Video> { CallBase = true };
            item.Object.Id = Guid.NewGuid(); item.Object.Name = "Test video"; item.Object.Path = input; item.Object.RunTimeTicks = 8 * Second;
            item.Setup(v => v.IsVisibleStandalone(owner)).Returns(true);
            item.Setup(v => v.IsAuthorizedToDownload(owner)).Returns(true);
            var source = new MediaSourceInfo { Id = "source", Path = input, RunTimeTicks = 8 * Second, MediaStreams = new[] { new MediaStream { Type = MediaStreamType.Video, Index = 0 }, new MediaStream { Type = MediaStreamType.Audio, Index = 1 } } };
            var library = new Mock<ILibraryManager>(); library.Setup(l => l.GetItemById<Video>(item.Object.Id)).Returns(item.Object);
            var users = new Mock<IUserManager>(); users.Setup(u => u.GetUserById(owner.Id)).Returns(owner);
            var sources = new Mock<IMediaSourceManager>(); sources.Setup(m => m.GetStaticMediaSources(item.Object, false, owner)).Returns(new[] { source });
            var controller = new ClipController(library.Object, users.Object, sources.Object, clips, NullLogger<ClipController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", owner.Id.ToString()) }, "test")) } } };
            var request = new ClipRequest { ItemId = item.Object.Id, MediaSourceId = source.Id, AnchorTicks = 3 * Second, BeforeTicks = Second, AfterTicks = Second, Preview = true };
            var created = Assert.IsType<OkObjectResult>(await controller.Create(request, CancellationToken.None));
            var id = JsonSerializer.SerializeToElement(created.Value).GetProperty("Id").GetGuid();
            var stored = Assert.IsType<StoredClip>(clips.Find(id, owner.Id));
            Assert.Null(clips.Find(id, Guid.NewGuid()));
            clips.Remove(id, Guid.NewGuid());
            Assert.True(File.Exists(stored.Path));
            var file = Assert.IsType<FileStreamResult>(controller.Get(id));
            Assert.True(file.EnableRangeProcessing); await file.FileStream.DisposeAsync();
            item.Setup(v => v.IsAuthorizedToDownload(owner)).Returns(false);
            Assert.IsType<NotFoundObjectResult>(controller.Get(id));
            Assert.IsType<NotFoundObjectResult>(await controller.Create(request, CancellationToken.None));
            clips.Remove(id, owner.Id);
            Assert.False(File.Exists(stored.Path));
            Assert.Null(clips.Find(id, owner.Id));

            // Cancellation kills a running encoder and permits temporary-file deletion.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var live = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { "-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-f", "null", "-" }) live.ArgumentList.Add(arg);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipEncoder.RunAsync(live, cts.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string> Run(string program, params string[] args)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }
}

internal sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCREENSHOT_FFMPEG"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCREENSHOT_FFPROBE")))
            Skip = "Set SCREENSHOT_FFMPEG and SCREENSHOT_FFPROBE to run real-media integration tests.";
    }
}
