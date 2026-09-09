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

    [Theory]
    [InlineData(70, 10, 40, 200)] // Entirely before the requested moment.
    [InlineData(70, 90, 130, 200)] // Entirely after the requested moment.
    [InlineData(70, 10, 130, 200)]
    [InlineData(5, 0, 3, 10)]
    [InlineData(198, 199, 200, 200)]
    public void EndpointsCanMoveAnywhereWithinFrozenWindow(long anchor, long start, long end, long runtime)
    {
        var result = ClipRange.FromRequest(new ClipRequest { AnchorTicks = anchor * Second, StartTicks = start * Second, EndTicks = end * Second }, runtime * Second);
        Assert.Equal(start * Second, result.StartTicks);
        Assert.Equal(end * Second, result.EndTicks);
    }

    [Theory]
    [InlineData(70, 9, 40, 200)]
    [InlineData(70, 90, 131, 200)]
    [InlineData(70, 50, 50, 200)]
    [InlineData(70, 60, 50, 200)]
    [InlineData(5, -1, 3, 10)]
    [InlineData(198, 199, 201, 200)]
    [InlineData(201, 199, 200, 200)]
    public void EndpointsCannotEscapeFrozenWindow(long anchor, long start, long end, long runtime)
        => Assert.Throws<ArgumentException>(() => ClipRange.FromRequest(new ClipRequest { AnchorTicks = anchor * Second, StartTicks = start * Second, EndTicks = end * Second }, runtime * Second));

    [Fact]
    public void EndpointsMustBePairedAndCannotOverflow()
    {
        Assert.Throws<ArgumentException>(() => ClipRange.FromRequest(new ClipRequest { AnchorTicks = Second, StartTicks = 0 }, 10 * Second));
        Assert.Throws<ArgumentException>(() => ClipRange.FromRequest(new ClipRequest { AnchorTicks = Second, EndTicks = Second }, 10 * Second));
        Assert.Throws<ArgumentException>(() => ClipRange.FromRequest(new ClipRequest { AnchorTicks = 1, StartTicks = long.MinValue, EndTicks = long.MaxValue }, long.MaxValue));
        var result = ClipRange.FromRequest(new ClipRequest { AnchorTicks = long.MaxValue - 2, StartTicks = long.MaxValue - 1, EndTicks = long.MaxValue }, long.MaxValue);
        Assert.Equal(1, result.DurationTicks);
    }

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

    [FfmpegFact]
    public async Task FastPreviewToneMapsHdrAndPreservesLowFrameRates()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SCREENSHOT_FFMPEG")!;
        var ffprobe = Environment.GetEnvironmentVariable("SCREENSHOT_FFPROBE")!;
        var root = Path.Combine(Path.GetTempPath(), "clip-preview-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            foreach (var rate in new[] { 12, 30 })
            foreach (var format in new[] { "mp4", "webm" })
            {
                var source = Path.Combine(root, $"hdr-{rate}.mkv");
                var preview = Path.Combine(root, $"preview-{rate}.{format}");
                await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-filter_threads", "2", "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate={rate}:duration=3", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", "-vf", "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc", "-y", source);
                await ClipEncoder.RunAsync(ClipEncoder.Build(ffmpeg, source, preview, new ClipRange(Second / 2, 5 * Second / 2), 0, null, true, null, null, true, format == "webm"), CancellationToken.None);
                var probe = JsonDocument.Parse(await Run(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", preview));
                var video = probe.RootElement.GetProperty("streams")[0];
                Assert.Equal(960, video.GetProperty("width").GetInt32());
                Assert.Equal(540, video.GetProperty("height").GetInt32());
                Assert.Equal("16:9", video.GetProperty("display_aspect_ratio").GetString());
                Assert.Equal($"{Math.Min(rate, 24)}/1", video.GetProperty("avg_frame_rate").GetString());
                Assert.Equal("bt709", video.GetProperty("color_transfer").GetString());
                Assert.Equal("bt709", video.GetProperty("color_primaries").GetString());
                Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 1.9, 2.1);
            }
        }
        finally { Directory.Delete(root, true); }
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
            Assert.Equal("25/1", video.GetProperty("avg_frame_rate").GetString());
            Assert.Contains(streams, s => s.GetProperty("codec_type").GetString() == "audio");
            var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 3.35, 3.5);
            var quickPreview = Path.Combine(root, "quick-preview.mp4");
            await ClipEncoder.RunAsync(ClipEncoder.Build(ffmpeg, input, quickPreview, new ClipRange(23 * Second / 10, 57 * Second / 10), 0, 1, true, null, null, false), CancellationToken.None);
            var quickProbe = JsonDocument.Parse(await Run(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", quickPreview));
            var quickVideo = quickProbe.RootElement.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("codec_type").GetString() == "video");
            Assert.Equal("24/1", quickVideo.GetProperty("avg_frame_rate").GetString());
            Assert.Equal("4:3", quickVideo.GetProperty("display_aspect_ratio").GetString());
            Assert.Equal("yuv420p", quickVideo.GetProperty("pix_fmt").GetString());
            Assert.Contains(quickProbe.RootElement.GetProperty("streams").EnumerateArray(), s => s.GetProperty("codec_type").GetString() == "audio");
            Assert.InRange(double.Parse(quickProbe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 3.3, 3.5);
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
            await ClipEncoder.RunAsync(ClipEncoder.Build(ffmpeg, black, captioned, new ClipRange(23 * Second / 10, 43 * Second / 10), 0, null, true, subtitle, null, false), CancellationToken.None);
            var previewStats = await Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", captioned, "-vf", "signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-");
            var previewLuma = System.Text.RegularExpressions.Regex.Matches(previewStats, @"lavfi.signalstats.YAVG=([0-9.]+)").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.True(previewLuma[0] > 16.1);
            Assert.InRange(previewLuma[^1], 15.9, 16.1);

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
            Assert.Equal("video/mp4", file.ContentType);
            var filmstrip = Assert.IsType<FileStreamResult>(controller.Filmstrip(id));
            Assert.Equal("image/jpeg", filmstrip.ContentType);
            Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
            Assert.True(filmstrip.FileStream.Length > 0);
            await filmstrip.FileStream.DisposeAsync();
            request.PreviewFormat = "webm";
            var webmCreated = Assert.IsType<OkObjectResult>(await controller.Create(request, CancellationToken.None));
            var webmId = JsonSerializer.SerializeToElement(webmCreated.Value).GetProperty("Id").GetGuid();
            var webmClip = Assert.IsType<StoredClip>(clips.Find(webmId, owner.Id));
            Assert.EndsWith(".webm", webmClip.Filename);
            var webmFile = Assert.IsType<FileStreamResult>(controller.Get(webmId));
            Assert.Equal("video/webm", webmFile.ContentType);
            Assert.True(webmFile.EnableRangeProcessing);
            await webmFile.FileStream.DisposeAsync();
            var webmProbe = JsonDocument.Parse(await Run(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", webmClip.Path));
            var webmStreams = webmProbe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.Contains(webmStreams, stream => stream.GetProperty("codec_name").GetString() == "vp8");
            Assert.Contains(webmStreams, stream => stream.GetProperty("codec_name").GetString() == "opus");
            Assert.Equal("4:3", webmStreams.Single(stream => stream.GetProperty("codec_type").GetString() == "video").GetProperty("display_aspect_ratio").GetString());
            Assert.InRange(double.Parse(webmProbe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 1.95, 2.1);
            var webmStrip = Assert.IsType<FileStreamResult>(controller.Filmstrip(webmId));
            Assert.True(webmStrip.FileStream.Length > 0);
            await webmStrip.FileStream.DisposeAsync();
            clips.Remove(webmId, owner.Id);
            Assert.False(File.Exists(webmClip.FilmstripPath));
            Assert.IsType<NotFoundObjectResult>(controller.Filmstrip(webmId));
            // Linked versions need their own permission check, including on later GETs.
            var alternatePath = Path.Combine(root, "alternate.mkv"); File.Copy(input, alternatePath);
            var alternate = new Mock<Video> { CallBase = true };
            alternate.Object.Id = Guid.NewGuid(); alternate.Object.Path = alternatePath;
            alternate.Setup(v => v.IsVisibleStandalone(owner)).Returns(true);
            library.Setup(l => l.GetItemById<Video>(alternate.Object.Id)).Returns(alternate.Object);
            var alternateSource = new MediaSourceInfo { Id = alternate.Object.Id.ToString("N"), Path = alternatePath, RunTimeTicks = 8 * Second, MediaStreams = source.MediaStreams };
            sources.Setup(m => m.GetStaticMediaSources(item.Object, false, owner)).Returns(new[] { source, alternateSource });
            request.MediaSourceId = alternateSource.Id;
            var alternateResult = Assert.IsType<OkObjectResult>(await controller.Create(request, CancellationToken.None));
            var alternateId = JsonSerializer.SerializeToElement(alternateResult.Value).GetProperty("Id").GetGuid();
            Assert.Equal(alternate.Object.Id, clips.Find(alternateId, owner.Id)!.SourceItemId);
            var alternateFile = Assert.IsType<FileStreamResult>(controller.Get(alternateId)); await alternateFile.FileStream.DisposeAsync();
            alternate.Setup(v => v.IsVisibleStandalone(owner)).Returns(false);
            Assert.IsType<NotFoundObjectResult>(controller.Get(alternateId));
            Assert.IsType<NotFoundObjectResult>(controller.Filmstrip(alternateId));
            clips.Remove(alternateId, owner.Id);
            request.MediaSourceId = source.Id;
            sources.Setup(m => m.GetStaticMediaSources(item.Object, false, owner)).Returns(new[] { source });
            request.PreviewFormat = "invalid";
            Assert.IsType<BadRequestObjectResult>(await controller.Create(request, CancellationToken.None));
            request.PreviewFormat = "mp4";
            // Exercise the new endpoint contract through the controller and real encoder.
            foreach (var selection in new[] { new ClipRange(Second / 2, 3 * Second / 2), new ClipRange(4 * Second, 5 * Second) })
            {
                var selectedRequest = new ClipRequest { ItemId = item.Object.Id, MediaSourceId = source.Id, AnchorTicks = 3 * Second, StartTicks = selection.StartTicks, EndTicks = selection.EndTicks, PreviewFormat = "webm" };
                var selectedResult = Assert.IsType<OkObjectResult>(await controller.Create(selectedRequest, CancellationToken.None));
                var selectedId = JsonSerializer.SerializeToElement(selectedResult.Value).GetProperty("Id").GetGuid();
                var selectedClip = Assert.IsType<StoredClip>(clips.Find(selectedId, owner.Id));
                Assert.Equal(selection, selectedClip.Range);
                Assert.IsType<NotFoundObjectResult>(controller.Filmstrip(selectedId));
                Assert.False(File.Exists(selectedClip.FilmstripPath));
                Assert.EndsWith(".mp4", selectedClip.Filename);
                Assert.Equal("video/mp4", selectedClip.ContentType);
                Assert.True(new FileInfo(selectedClip.Path).Length > 0);
                clips.Remove(selectedId, owner.Id);
            }
            // Screenshots apply the same access policy before and after rendering.
            var screenshotController = new ScreenshotController(library.Object, users.Object, sources.Object, encoder.Object, Mock.Of<ISubtitleEncoder>(), NullLogger<ScreenshotController>.Instance)
            { ControllerContext = controller.ControllerContext };
            var screenshot = Assert.IsType<FileContentResult>(await screenshotController.CaptureScreenshot(item.Object.Id, 2 * Second, source.Id, null, CancellationToken.None));
            Assert.Equal("image/jpeg", screenshot.ContentType);
            Assert.True(screenshot.FileContents.Length > 0);
            Assert.Equal("private, no-store", screenshotController.Response.Headers.CacheControl.ToString());
            encoder.SetupGet(e => e.EncoderPath).Returns(() => { item.Setup(v => v.IsVisibleStandalone(owner)).Returns(false); return ffmpeg; });
            Assert.IsType<NotFoundObjectResult>(await screenshotController.CaptureScreenshot(item.Object.Id, 2 * Second, source.Id, null, CancellationToken.None));
            // Previously generated clips are also denied when library visibility changes.
            Assert.IsType<NotFoundObjectResult>(controller.Get(id));
            Assert.IsType<NotFoundObjectResult>(controller.Filmstrip(id));
            item.Setup(v => v.IsVisibleStandalone(owner)).Returns(true);
            encoder.SetupGet(e => e.EncoderPath).Returns(ffmpeg);

            // A second user's valid identity cannot stream or remove another user's clip.
            var otherUser = new User("other", "auth", "reset");
            users.Setup(u => u.GetUserById(otherUser.Id)).Returns(otherUser);
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", otherUser.Id.ToString()) }, "test"));
            Assert.IsType<NotFoundObjectResult>(controller.Get(id));
            Assert.IsType<NotFoundObjectResult>(controller.Filmstrip(id));
            Assert.IsType<NoContentResult>(controller.Delete(id));
            Assert.True(File.Exists(stored.Path));
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", owner.Id.ToString()) }, "test"));

            item.Setup(v => v.IsAuthorizedToDownload(owner)).Returns(false);
            Assert.IsType<NotFoundObjectResult>(controller.Get(id));
            Assert.IsType<NotFoundObjectResult>(controller.Filmstrip(id));
            Assert.IsType<NotFoundObjectResult>(await controller.Create(request, CancellationToken.None));
            clips.Remove(id, owner.Id);
            Assert.False(File.Exists(stored.Path));
            Assert.False(File.Exists(stored.FilmstripPath));
            Assert.Null(clips.Find(id, owner.Id));

            // Cancellation kills a running encoder and permits temporary-file deletion.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var live = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { "-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-f", "null", "-" }) live.ArgumentList.Add(arg);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClipEncoder.RunAsync(live, cts.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FilmstripCoversWholePreviewIncludingSingleFrame(bool webm, bool singleFrame)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SCREENSHOT_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("SCREENSHOT_FFPROBE");
        if (string.IsNullOrEmpty(ffmpeg) || string.IsNullOrEmpty(ffprobe)) return;
        var root = Path.Combine(Path.GetTempPath(), "filmstrip-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "source.mkv");
            var preview = Path.Combine(root, webm ? "preview.webm" : "preview.mp4");
            var strip = Path.Combine(root, "filmstrip.jpg");
            // Every second has a different gray value, so blank/repeated tail tiles fail.
            await Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "nullsrc=s=160x90:r=1:d=8,geq=lum='32+N*24':cb=128:cr=128", "-c:v", "ffv1", "-threads", "2", "-y", input);
            var duration = singleFrame ? Second / 25 : 8 * Second;
            await ClipEncoder.RunAsync(ClipEncoder.Build(ffmpeg, input, preview, new ClipRange(0, duration), 0, null, true, null, null, false, webm), CancellationToken.None);
            await ClipEncoder.RunAsync(ClipEncoder.BuildFilmstrip(ffmpeg, preview, strip, duration), CancellationToken.None);
            using var probe = JsonDocument.Parse(await Run(ffprobe, "-v", "error", "-show_streams", "-of", "json", strip));
            var stream = probe.RootElement.GetProperty("streams")[0];
            Assert.Equal(1280, stream.GetProperty("width").GetInt32());
            Assert.Equal(90, stream.GetProperty("height").GetInt32());
            Assert.Equal("mjpeg", stream.GetProperty("codec_name").GetString());
            for (var index = 0; index < 8; index++)
            {
                var stats = await Run(ffmpeg, "-v", "error", "-threads", "2", "-filter_threads", "2", "-i", strip, "-vf", $"crop=160:90:{index * 160}:0,scale=in_range=full:out_range=limited,format=yuv420p,signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-");
                var match = System.Text.RegularExpressions.Regex.Match(stats, @"lavfi.signalstats.YAVG=([0-9.]+)");
                var luma = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var expected = 32 + (singleFrame ? 0 : index * 24);
                Assert.InRange(luma, expected - 4, expected + 4);
            }
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
