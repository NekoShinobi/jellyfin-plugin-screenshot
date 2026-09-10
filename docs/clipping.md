# Video clipping

[Back to the README](../README.md)

## Create a clip

1. Click the scissors button beside Screenshot. The main player pauses while the server prepares a preview.
2. Choose a **Starting point** and **Duration**, or drag the timeline handles to select a range.
3. Preview the selection, optionally include the selected subtitles, and create your MP4.

The available window is fixed when you open the editor: up to 60 seconds before
and 60 seconds after that moment, limited by the video's start and end. **Your
moment** is a reference marker; the selection can sit entirely before or after it.
The main player stays paused when you close the editor.

## Controls

| Control | Behavior |
| --- | --- |
| Timeline handles | Drag to trim; the preview shows the boundary being moved. |
| Mouse wheel over a handle | Move that boundary by one second, or five with Shift. |
| Arrow keys on a handle | Adjust the boundary; Shift uses five-second steps, and Home/End move to the available limits. |
| Thumbnail reel | Click or drag to scrub anywhere in the window without changing the selection. Playback pauses and stays paused on release. |
| Arrow keys on the reel | Seek through the preview; Shift uses five-second steps, and Home/End seek to the window boundaries. |
| Starting point / Duration | Enter times in seconds. Starting point is measured from the beginning of the source video; changing Duration previews the end frame. |
| Set start here / Set end here | Use the current preview position as a selection boundary. |
| Presets | Quickly select the last 30 seconds, a range around your moment, or the full window. |
| Position badge | Shows the current timestamp in the source video. |

Moving the starting point preserves the duration until the selection reaches the
window's end. If **Set start here** or **Set end here** would cross the opposite
boundary, that boundary moves too, preserving the previous duration where the
window allows it. An empty selection recovers with up to one second. Buttons are
disabled at edges that would leave an empty clip.

## Preview and export

- **Preview:** up to 960×540 at 24 fps, retaining lower source frame rates. The 16:9 preview box fits the whole image with black bars where needed.
- **Export:** MP4 with H.264 video and stereo AAC audio, using the selected audio track. Exports keep the source display aspect ratio and frame rate; preview black bars are not added to the file.
- **Subtitles and HDR:** selected text or bitmap subtitles can be burned in. PQ/HLG video is tone-mapped to SDR.
- **Precision:** clips are re-encoded so trims can fall between source keyframes. H.264 requires even dimensions; odd dimensions are rounded down by one pixel while preserving display aspect ratio.

The preview uses H.264/AAC MP4 when supported. Clients without those codecs,
including some Jellium/Desktop builds, request VP8/Opus WebM. MP4 decode failures
retry once with WebM when supported. Exports always use H.264/AAC MP4.

The server renders the full window before playback, so preparation time depends on
server CPU and source decoding speed. It resizes preview frames before HDR and
text-subtitle processing and uses fast encoding settings. Preview compression is
less efficient; files can be larger within the configured bitrate limit.

All eight filmstrip thumbnails come from one JPEG generated from the finished
preview. **Retry thumbnails** reloads that image without rendering another preview.

## Requirements and temporary files

Clipping requires a local video with a known duration and permission to both play
and download it. Live/remote streams and external audio tracks are unsupported.
Previews, filmstrips, and downloads enforce ownership and current media access.

| Limit | Behavior |
| --- | --- |
| Active renders | Two globally, one per user. |
| Render timeout | Ten minutes. Closing the editor cancels an active render. |
| Previews and filmstrips | Released when the editor closes. |
| Ready downloads | Expire after 30 minutes; older downloads may be evicted when creating more clips. |
| Temporary storage | Bounded and stored under Jellyfin's cache directory. |

Failed previews can be retried. Downloads use the browser's save flow or the
Desktop client's native download API. For loading failures, see
[preview troubleshooting](../README.md#preview-troubleshooting).
