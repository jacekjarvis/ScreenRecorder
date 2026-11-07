# Jarvo Screen Recorder

A Windows screen recorder and video editor that works like FlashBack Pro 5's Recorder and Player,
with a blue theme and rounded, modern windows. Original code; see [SPEC.md](SPEC.md) for the feature plan and [docs/UX.md](docs/UX.md)
for the look and feel being matched.

## Version 1 — what works

**Recorder**
- Area: full screen (each monitor), all monitors, a window (click it), or a region (move/resize the red frame)
- PC sounds and microphone, each on its own track; webcam
- Pause / resume, restart, markers, record-time notes (push the mouse to a screen edge)
- Hotkeys, tray icon menu, floating controls, Set Timer (scheduled start/stop)
- Size limit, automatic stop when the disk is nearly full, hide desktop icons
- Long recordings: tested design target is 8 hours (no 4 GB file limits; a crash keeps what was recorded)
- The app's own windows (red frame, toolbar, notes) never appear in recordings; the red frame closes when you stop
- Every recording is saved as a `.jsrec` file the moment you stop: double-click it any time to open it in the
  Video Editor and export later

**Video Editor**
- Play with sound, step frame by frame, jump to markers
- Select frames on the timeline (drag, Shift+click, `[` / `]`, or type frame numbers)
- Crop to selection, or delete frames from the start or end; undo/redo; edits are saved with the recording
- Export to MP4 — from frame X to frame Y (or the whole movie), video quality and sound quality (both default to Highest), cursor/sound/webcam on or off,
  graphics-card encoding; screenshot of the current frame as PNG

## Requirements

- Windows 10 (2004) or later; Windows 11 recommended
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- FFmpeg: put `ffmpeg.exe` next to `JarvoScreenRecorder.exe`, or install it with `winget install Gyan.FFmpeg`

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
| `<recording name>.jsrec` | The recording file (JSON): size, frame rate, markers, notes, the editor's crop. Double-click to open |

The cursor is stored as data so it can be drawn, hidden or restyled at export time.

## Project layout

- `src/ScreenRecorder.Core` — capture (Windows Graphics Capture), audio (WASAPI), webcam, input logging, export
- `src/ScreenRecorder.App` — WPF app: Recorder, Video Editor, Settings, tray
- `src/ScreenRecorder.DevCli` — developer tool: `record`, `export`, `info` without the UI
- `tests/ScreenRecorder.Tests` — unit tests and a frame-accurate trim test (needs FFmpeg)
- `tools/make-icon.ps1` — regenerates the app icon

The app registers the `.jsrec` file type for the current user (no admin rights) the first time it runs.
