# Jellyfin Screenshot Capture Plugin

Adds a camera button to the Jellyfin video player OSD. On click, choose whether to include the currently selected subtitle track. The server extracts the exact frame via FFmpeg and downloads it as a JPEG.

## Video clipping

The scissors button beside Screenshot opens the **Focus** editor and pauses playback.
The request timestamp fixes a window from 60 seconds before to 60 seconds after
it (120 seconds total), clamped to the media's start
and end. Choose a **Starting point** (seconds into the video) and **Duration**, or
drag either handle anywhere within that window. “Your moment” is a reference marker;
a selection may sit entirely before or after it. Moving the starting point preserves
the duration until it reaches the window's end. Drag the timeline handles, enter times, use presets, or trim with arrow keys
(Shift changes by five seconds; Home/End move to the available limits).

The server prepares an H.264/AAC preview of the surrounding window at up to
960×540 and 24 fps, retaining lower source frame rates. Preview frames are resized
before HDR tone mapping and text subtitle rendering, then encoded with the fastest
software preset. Frequent keyframes help seeking and thumbnail extraction. Preview
compression is less efficient, so files can be larger within the existing bitrate
limit; export quality and frame rate are unchanged. The complete window still needs
to render before playback, so preparation time depends on server CPU and source
decoding speed. It supports real playback, seeking, and a thumbnail filmstrip. The **16:9 preview box** fits the
entire image with black bars as needed. These bars are **never added to the export**.
MP4 exports retain the source display aspect ratio, use the selected audio track,
and optionally burn in the selected text or bitmap subtitle track. H.264 requires
even dimensions; odd dimensions are rounded down by one pixel with display aspect
ratio preserved. PQ/HLG sources are tone-mapped to SDR. Clips are re-encoded rather
than copied at keyframe boundaries so trimming can start between keyframes.

Clipping requires a local video with a known duration and a user allowed to play
and download it. External audio and live/remote streams are unsupported. Clips use
stereo AAC audio. The main player stays paused after closing the editor.

Preparing/exporting shows a busy state; closing the dialog cancels an in-progress
request and its FFmpeg process. Failed previews can be retried. Downloads use the
browser's save flow or Jellyfin Desktop's native download API. Preview files are
released on close; ready downloads expire after 30 minutes. The server allows two
renders globally, one per user, with a ten-minute timeout and bounded temporary
storage under its cache directory. Older completed downloads may be evicted when a
user creates additional clips. The editor respects existing download permissions.

## Requirements

- Jellyfin 10.11.x or 12.x (install the matching build below)
- Jellyfin FFmpeg configured in Jellyfin (H.264/libx264, AAC, libass, and zscale/tonemap for HDR)
- [jellyfin-plugin-file-transformation](https://github.com/jellyfin/jellyfin-plugin-file-transformation) (optional, for script injection without touching `index.html`)

## Desktop Client

Should work with [jellyfin-desktop](https://github.com/jellyfin/jellyfin-desktop) w/ latest version.

Creates a screenshot button that will save the screenshot in the same folder as the executable - <img width="483" height="121" alt="image" src="https://github.com/user-attachments/assets/2acf3f03-83bb-4d46-bae0-402aa386b0f1" />

## Installation

### Plugin repository

Add the following URL under **Dashboard > Plugins > Repositories**:

```text
https://raw.githubusercontent.com/NekoShinobi/jellyfin-plugin-screenshot/main/manifest.json
```

### Manual installation

1. Download the release zip
2. Choose the archive for your server: plugin **2.x** for Jellyfin **10.11.x**, or plugin **3.x** for Jellyfin **12.x**. Extract it into its own folder under your Jellyfin `plugins/` directory, replacing any older Screenshot Capture installation.
3. Restart Jellyfin

## Compatibility and building

Both server versions use the same capture code and embedded player script. Separate
assemblies are required because Jellyfin 10.11 uses .NET 9 and Jellyfin 12 uses .NET 10.

| Server | Plugin series | Framework | Minimum server ABI |
| --- | --- | --- | --- |
| Jellyfin 10.11.x | 2.x | net9.0 | 10.11.0.0 |
| Jellyfin 12.x | 3.x | net10.0 | 12.0.0.0 |

Use the .NET 10 SDK to build both targets (the .NET 9 SDK can build only the 10.11 target):

```bash
# Jellyfin 10.11 (default)
dotnet build -c Release

# Jellyfin 12
dotnet build -c Release -p:JellyfinVersion=12.0.0
```

The packaged DLL and `meta.json` are written to:

- `Jellyfin.Plugin.Screenshot/bin/Release/net9.0/Screenshot Capture_2.2.3.0/`
- `Jellyfin.Plugin.Screenshot/bin/Release/net10.0/Screenshot Capture_3.2.3.0/`

Intermediate files are isolated by server version, so switching between builds does
not require cleaning. `build.yaml` describes the default 10.11 package;
`build-12.yaml` describes the 12 package, which must be built with
`-p:JellyfinVersion=12.0.0`.

Pushes and pull requests build downloadable archives for **both** server versions.
Every commit pushed to `main` publishes both archives in one GitHub release and adds
two entries to `manifest.json`, using each archive's actual version and target ABI.
The fourth version component is the workflow run number. Jellyfin 12 uses a higher
plugin version series so upgrading the server also selects the .NET 10 plugin build.
Existing 1.0.x manifest entries remain available for older installations.
The workflow can also be run manually with custom release notes.

## Disclaimer

> **This plugin was vibecoded.** It was built with AI assistance and has not been audited for production use. Use at your own risk.

## License

[MIT](LICENSE)

## Validation

Run the .NET tests for either target (install both .NET runtimes):

```bash
dotnet test tests/Jellyfin.Plugin.Screenshot.Tests -c Release -p:JellyfinVersion=10.11.0
dotnet test tests/Jellyfin.Plugin.Screenshot.Tests -c Release -p:JellyfinVersion=12.0.0
```

Set `SCREENSHOT_FFMPEG` and `SCREENSHOT_FFPROBE` to executable paths to include the
real-media tests for duration, aspect ratio, audio, subtitle timing, cancellation,
permissions, ownership, and file cleanup. Without these variables that test is skipped.

`tests/browser-check.py` uses Playwright and an isolated HTTP fixture with the actual
injected plugin scripts. Set `CLIP_FIXTURE_VIDEO` to a 120-second H.264/AAC MP4 and
optionally `CLIP_BROWSER_OUTPUT` for screenshots and results. It verifies preview
playback, filmstrip generation, trim bounds, keyboard/pointer controls, fixed anchors,
selected tracks, browser/native downloads, retry/cancellation, and mobile media bounds.
This fixture does not replace testing against a deployed Jellyfin server.

### Authentication compatibility

Session lookup and clip creation/cleanup use the signed-in client’s token in the standard `Authorization: MediaBrowser` header. Preview video and native downloads use Jellyfin’s supported `ApiKey` query parameter. Both Jellyfin 10.11 and 12 work with legacy authorization disabled; no server authentication setting needs to change.

### Preview troubleshooting

The editor shows elapsed time while the server renders the preview, then switches to “Loading preview video” once the MP4 is ready. A media load that stalls for 30 seconds offers a retry. Render requests have a client deadline of 610 seconds, allowing the server’s 10-minute limit to return its error; closing the editor cancels preparation. Temporary-file cleanup requests are limited to 5 seconds.

If preparation stalls, check whether `POST /Screenshot/clips` is still pending or whether the subsequent `GET /Screenshot/clips/<id>` fails. Jellyfin server logs now record the clip ID, render start, completion time, and output size. Remove API keys before sharing request details.

Preview controls stay visible during playback, including when the underlying player hides its idle cursor. Editor clicks and keys stay within the modal, and the play/pause button retains its hit target during time updates. Absolute `StartTicks`/`EndTicks` are validated against the frozen `AnchorTicks` window; earlier clients using `BeforeTicks`/`AfterTicks` remain supported.

### Screenshot save notifications

With the updated Jellyfin Desktop download bridge, the toast shows **Screenshot saved to:** followed by the actual full path after the download completes. This includes folder or filename changes in the save dialog. Cancellation and failed downloads have separate messages. Long paths wrap and remain visible for ten seconds.

Standard browsers do not expose the local download destination or completion to page scripts. Their toast shows the requested filename and directs you to the browser's download history. Older Desktop clients that only expose `startDownload` also keep working, but cannot report the full path. Installing the plugin alone does not add the required Desktop capability.

The accompanying Desktop change exposes `jmpNative.startDownloadWithResult(url, requestId)` and emits `jellyfin-download-result` with `requestId`, `status` (`complete`, `cancelled`, or `failed`), and `fullPath` on completion. The plugin ignores unrelated/duplicate events. Desktop limits pending requests and delivers results only to the initiating page.
