# Jellyfin Screenshot Capture Plugin

Adds a camera button to the Jellyfin video player OSD. On click, choose whether to include the currently selected subtitle track. The server extracts the exact frame via FFmpeg and downloads it as a JPEG.

## Video clipping

The scissors button beside Screenshot opens the **Focus** editor and pauses playback.
The request timestamp stays fixed while editing. Choose up to 60 seconds before and
60 seconds after it (120 seconds total), with the window clamped to the media's start
and end. Drag the timeline handles, enter times, use presets, or trim with arrow keys
(Shift changes by five seconds; Home/End move to the available limits).

The server prepares a small H.264/AAC preview of the surrounding window. It supports
real playback, seeking, and a thumbnail filmstrip. The **16:9 preview box** fits the
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

- `Jellyfin.Plugin.Screenshot/bin/Release/net9.0/Screenshot Capture_2.1.0.0/`
- `Jellyfin.Plugin.Screenshot/bin/Release/net10.0/Screenshot Capture_3.1.0.0/`

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
