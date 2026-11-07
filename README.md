# ScreenRecorder

A Windows screen recorder and video editor that works like FlashBack Pro 5's Recorder and Player,
with a blue theme. Original code; see [SPEC.md](SPEC.md) for the feature plan and [docs/UX.md](docs/UX.md)
for the look and feel being matched.

## Version 1 — what works

**Recorder**
- Area: full screen (each monitor), all monitors, a window (click it), or a region (move/resize the red frame)
- PC sounds and microphone, each on its own track; webcam
- Pause / resume, restart, markers, record-time notes (push the mouse to a screen edge)
- Hotkeys, tray icon menu, floating controls, Set Timer (scheduled start/stop)
- Size limit, automatic stop when the disk is nearly full, hide desktop icons
- Long recordings: tested design target is 8 hours (no 4 GB file limits; a crash keeps what was recorded)
- ScreenRecorder's own windows never appear in recordings

**Video Editor**
- Play with sound, step frame by frame, jump to markers
- Select frames on the timeline (drag, Shift+click, `[` / `]`, or type frame numbers)
- Crop to selection, or delete frames from the start or end; undo/redo; edits are saved with the recording
- Export to MP4 — video quality and sound quality (both default to Highest), cursor/sound/webcam on or off,
  graphics-card encoding; screenshot of the current frame as PNG

## Requirements

- Windows 10 (2004) or later; Windows 11 recommended
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- FFmpeg: put `ffmpeg.exe` next to `ScreenRecorder.exe`, or install it with `winget install Gyan.FFmpeg`

## Build and run

```bash
dotnet build ScreenRecorder.sln -c Release
```

```bash
dotnet run --project src/ScreenRecorder.App -c Release
```

```bash
dotnet test tests/ScreenRecorder.Tests
```

## How recordings are stored

Each recording is a folder (default: `Videos\ScreenRecorder\Recording <date time>`):

| File | Content |
|---|---|
| `screen.mkv` | Screen video, H.264, recorded **without** the cursor |
| `system.pcm`, `mic.pcm` | PC sounds / microphone, raw 16-bit PCM, aligned to the video clock |
| `webcam.mkv` | Webcam video |
| `cursor.jsonl`, `cursors/` | Cursor position and image for every frame |
| `mouse.jsonl`, `keys.jsonl` | Clicks and (optional) keystrokes |
| `project.json` | Size, frame rate, markers, notes, and the editor's trim |

The cursor is stored as data so it can be drawn, hidden or restyled at export time.

## Project layout

- `src/ScreenRecorder.Core` — capture (Windows Graphics Capture), audio (WASAPI), webcam, input logging, export
- `src/ScreenRecorder.App` — WPF app: Recorder, Video Editor, Settings, tray
- `src/ScreenRecorder.DevCli` — developer tool: `record`, `export`, `info` without the UI
- `tests/ScreenRecorder.Tests` — unit tests and a frame-accurate trim test (needs FFmpeg)
