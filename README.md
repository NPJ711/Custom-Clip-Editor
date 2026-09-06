# Custom Clip Editor

A small Windows desktop app for turning gameplay captures into short montages
that fit inside a Discord upload limit.

Open a recording, mark an in and out point on the timeline, queue as many trims
as you like, then export the whole thing as one MP4 (or as audio only).

## Features

- **Filmstrip timeline** with a playhead and draggable in/out handles; frames
  outside the selection are dimmed
- **Play Selection** to preview just the trimmed range before queueing it
- **Frame stepping** and keyboard shortcuts
- **Clip queue** with reordering, per-clip and running-total durations
- **Size targets** — Discord 50 MB / 20 MB / full quality. The video bitrate is
  computed to fit the selected cap, so the export lands under it
- **Vertical 9:16 crop** and a burned-in caption, both optional
- Hardware encoding via `h264_nvenc` when available, `libx264` otherwise
- Export progress with cancel; drag-and-drop a video onto the window

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| `Space` | Play / pause |
| `[` / `]` | Set in point / out point |
| `,` / `.` | Step one frame back / forward |
| `←` / `→` | Skip 5 seconds back / forward |
| `P` | Play just the selection |

## Requirements

- Windows 10 or 11
- **FFmpeg** — the app will not export without it

Install FFmpeg with one command:

```
winget install Gyan.FFmpeg
```

Then restart the app. It also looks for `ffmpeg.exe` next to its own
executable, so you can drop the binaries in the app folder instead if you
prefer not to install anything system-wide.

The prebuilt download is self-contained — it does **not** require .NET to be
installed.

## Installing

Grab the zip from [Releases](../../releases), extract it anywhere, and run
`ClipEditor.exe`.

The executable is not code-signed, so Windows SmartScreen will show a warning
the first time you run it. Click **More info → Run anyway**.

## Settings

Stored at `%AppData%\ClipEditor\settings.json`: export folder, FFmpeg location,
last used folders, selected preset, and window placement. Delete the file to
reset to defaults.

## Building from source

Requires the .NET 10 SDK.

```
dotnet build ClipEditor.slnx
```

- `update-app.cmd` refreshes the local copy at `app\` that a desktop shortcut
  can point to.
- `make-release.cmd` produces the self-contained, distributable zip.

## Licence

This project is released under the [MIT licence](LICENSE).

### Third-party components

| Component | Licence |
| --- | --- |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) and [LibVLC](https://www.videolan.org/vlc/libvlc.html) (VideoLAN) | LGPL-2.1-or-later |
| [WPF-UI](https://github.com/lepoco/wpfui) | MIT |
| [FFMpegCore](https://github.com/rosenbjerg/FFMpegCore) | MIT |

LibVLC is used as a dynamically linked library and is redistributed unmodified
in the release archive, as LGPL-2.1 permits.

[FFmpeg](https://ffmpeg.org/) is **not** bundled — it is invoked as a separate
program and must be installed by the user, so no FFmpeg code is redistributed
here.
