using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Screenshot.Controllers;
using Jellyfin.Plugin.Screenshot.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Screenshot.Tests;

public sealed class AccessTests
{
    [Theory]
    [InlineData("library")]
    [InlineData("playback")]
    [InlineData("download")]
    [InlineData("disabled")]
    [InlineData("missing-user")]
    [InlineData("missing-item")]
    [InlineData("missing-claim")]
    [InlineData("invalid-claim")]
    public async Task DeniedUsersCannotResolveSourcesOrStartRendering(string denial)
    {
        var user = Account();
        if (denial == "playback") user.Permissions.Single(p => p.Kind == PermissionKind.EnableMediaPlayback).Value = false;
        if (denial == "download") user.Permissions.Single(p => p.Kind == PermissionKind.EnableContentDownloading).Value = false;
        if (denial == "disabled") user.Permissions.Add(new Permission(PermissionKind.IsDisabled, true));
        var item = new Mock<Video> { CallBase = true };
        item.Object.Id = Guid.NewGuid();
        item.Setup(v => v.IsVisibleStandalone(user)).Returns(denial != "library");
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById<Video>(item.Object.Id)).Returns(denial == "missing-item" ? null : item.Object);
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(user.Id)).Returns(denial == "missing-user" ? null : user);
        var sources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        var subtitles = new Mock<ISubtitleEncoder>(MockBehavior.Strict);
        var context = Context(denial == "missing-claim" ? null : denial == "invalid-claim" ? "bad-id" : user.Id.ToString());
        var screenshots = new ScreenshotController(library.Object, users.Object, sources.Object, encoder.Object, subtitles.Object, NullLogger<ScreenshotController>.Instance) { ControllerContext = context };
        var clips = new ClipController(library.Object, users.Object, sources.Object, null!, NullLogger<ClipController>.Instance) { ControllerContext = context };
        Assert.IsType<NotFoundObjectResult>(await screenshots.CaptureScreenshot(item.Object.Id, 0, null, null, CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await clips.Create(new ClipRequest { ItemId = item.Object.Id, BeforeTicks = 1 }, CancellationToken.None));
        sources.VerifyNoOtherCalls(); encoder.VerifyNoOtherCalls(); subtitles.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("restricted-version")]
    [InlineData("unknown-version")]
    [InlineData("mismatched-path")]
    [InlineData("unlisted-source")]
    public async Task AlternateSourceCannotBypassItemAccess(string scenario)
    {
        var user = Account();
        var main = new Mock<Video> { CallBase = true };
        main.Object.Id = Guid.NewGuid(); main.Object.Path = "/allowed/main.mkv";
        main.Setup(v => v.IsVisibleStandalone(user)).Returns(true);
        var alternate = new Mock<Video> { CallBase = true };
        alternate.Object.Id = Guid.NewGuid(); alternate.Object.Path = "/restricted/alternate.mkv";
        alternate.Setup(v => v.IsVisibleStandalone(user)).Returns(scenario != "restricted-version");
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById<Video>(main.Object.Id)).Returns(main.Object);
        library.Setup(l => l.GetItemById<Video>(alternate.Object.Id)).Returns(scenario == "unknown-version" ? null : alternate.Object);
        var users = new Mock<IUserManager>(); users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        var source = new MediaSourceInfo { Id = alternate.Object.Id.ToString("N"), Path = scenario == "mismatched-path" ? "/different/file.mkv" : alternate.Object.Path };
        var sources = new Mock<IMediaSourceManager>();
        sources.Setup(s => s.GetStaticMediaSources(main.Object, false, user)).Returns(new[] { source });
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        var context = Context(user.Id.ToString());
        var screenshots = new ScreenshotController(library.Object, users.Object, sources.Object, encoder.Object, Mock.Of<ISubtitleEncoder>(), NullLogger<ScreenshotController>.Instance) { ControllerContext = context };
        var clips = new ClipController(library.Object, users.Object, sources.Object, null!, NullLogger<ClipController>.Instance) { ControllerContext = context };
        var sourceId = scenario == "unlisted-source" ? Guid.NewGuid().ToString("N") : source.Id;
        var screenshot = await screenshots.CaptureScreenshot(main.Object.Id, 0, sourceId, null, CancellationToken.None);
        var clip = await clips.Create(new ClipRequest { ItemId = main.Object.Id, MediaSourceId = sourceId, BeforeTicks = 1 }, CancellationToken.None);
        Assert.Equal(scenario == "unlisted-source" ? 400 : 404, Assert.IsAssignableFrom<ObjectResult>(screenshot).StatusCode);
        Assert.Equal(scenario == "unlisted-source" ? 400 : 404, Assert.IsAssignableFrom<ObjectResult>(clip).StatusCode);
        encoder.VerifyNoOtherCalls();
    }

    private static User Account()
    {
        var user = new User("viewer", "auth", "reset");
        user.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
        user.Permissions.Add(new Permission(PermissionKind.EnableContentDownloading, true));
        return user;
    }

    private static ControllerContext Context(string? userId) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            userId is null ? Array.Empty<Claim>() : new[] { new Claim("Jellyfin-UserId", userId) }, "test")) }
    };
}
