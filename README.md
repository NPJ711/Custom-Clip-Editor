# Custom Clip Editor

A small Windows desktop app for turning gameplay captures into short montages
that fit inside a Discord upload limit.

Open a recording, mark an in and out point on the timeline, queue as many trims
as you like, then export the whole thing as one MP4 (or as audio only).

## Features

- **Filmstrip timeline** with a playhead and draggable in/out handles; frames
  outside the selection are dimmed
- **Play Selection** to preview just the trimmed range before queueing it
- **Frame stepping** and keyboard shortcuts: `Space`, `[` `]` (set in/out),
  `,` `.` (frame step), `←` `→` (±5s), `P` (play selection)
- **Clip queue** with reordering, per-clip and running-total durations
- **Size targets** — Discord 50 MB / 20 MB / full quality. The video bitrate is
  computed to fit the selected cap, so the export lands under it
- **Vertical 9:16 crop** and a burned-in caption, both optional
- Hardware encoding via `h264_nvenc` when available, `libx264` otherwise
- Export progress with cancel; drag-and-drop a video onto the window

## Requirements

- Windows, .NET 10
- [FFmpeg](https://ffmpeg.org/) — found automatically on `PATH` or in a few
  common install folders

## Settings

Stored at `%AppData%\ClipEditor\settings.json`: export folder, FFmpeg location,
last used folders, selected preset, and window placement. Delete the file to
reset to defaults.

## Building

```
dotnet build ClipEditor.slnx
```

To produce the standalone copy used by a desktop shortcut, run `update-app.cmd`.
