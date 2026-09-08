# Jellyfin Screenshot Capture Plugin

Adds a camera button to the Jellyfin video player OSD. On click, choose whether to include the currently selected subtitle track. The server extracts the exact frame via FFmpeg and downloads it as a JPEG.

## Requirements

- Jellyfin 10.11.x or 12.x (install the matching build below)
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
2. Choose the archive for your server: plugin **2.0.x** for Jellyfin **10.11.x**, or plugin **3.0.x** for Jellyfin **12.x**. Extract it into its own folder under your Jellyfin `plugins/` directory, replacing any older Screenshot Capture installation.
3. Restart Jellyfin

## Compatibility and building

Both server versions use the same capture code and embedded player script. Separate
assemblies are required because Jellyfin 10.11 uses .NET 9 and Jellyfin 12 uses .NET 10.

| Server | Plugin series | Framework | Minimum server ABI |
| --- | --- | --- | --- |
| Jellyfin 10.11.x | 2.0.x | net9.0 | 10.11.0.0 |
| Jellyfin 12.x | 3.0.x | net10.0 | 12.0.0.0 |

Use the .NET 10 SDK to build both targets (the .NET 9 SDK can build only the 10.11 target):

```bash
# Jellyfin 10.11 (default)
dotnet build -c Release

# Jellyfin 12
dotnet build -c Release -p:JellyfinVersion=12.0.0
```

The packaged DLL and `meta.json` are written to:

- `Jellyfin.Plugin.Screenshot/bin/Release/net9.0/Screenshot Capture_2.0.0.0/`
- `Jellyfin.Plugin.Screenshot/bin/Release/net10.0/Screenshot Capture_3.0.0.0/`

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
