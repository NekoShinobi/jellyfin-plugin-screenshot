# Jellyfin Screenshot Capture Plugin

Adds a camera button to the Jellyfin video player OSD. On click, the server extracts the exact frame via FFmpeg and downloads it as a JPEG.

## Requirements

- Jellyfin 10.11.0+
- FFmpeg configured in Jellyfin
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
2. Extract it into a folder under your Jellyfin `plugins/` directory
3. Restart Jellyfin

## Building

```bash
dotnet build -c Release
```

Output: `Jellyfin.Plugin.Screenshot/Screenshot Capture_1.0.0.0/`

Pushes and pull requests build a downloadable workflow artifact. To publish a
version, manually run the **Build and release** workflow from the `main` branch
and enter its changelog. The manual run replaces the fourth version component
with the workflow run number, creates the GitHub release, and adds that version
to `manifest.json`.

## Disclaimer

> **This plugin was vibecoded.** It was built with AI assistance and has not been audited for production use. Use at your own risk.

## License

[MIT](LICENSE)
