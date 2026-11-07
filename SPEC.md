# ScreenRecorder — spec

A Windows screen recorder and editor with the features chosen from FlashBack Pro 5.
This is original code. FlashBack's help and the app are used as a feature reference only.

## Stack

- C# / .NET 10, WPF UI
- Screen capture: Windows.Graphics.Capture (per monitor, window or region)
- Audio: WASAPI (system loopback + microphone), via NAudio
- Webcam: Media Foundation
- Encoding and export: FFmpeg (bundled LGPL build)
- Global hotkeys and keystroke/mouse logging: low-level Win32 hooks

## Core design: record raw, render effects later

A recording is a **project folder**, not a flat video:

| Track | Content |
|---|---|
| `screen.mkv` | Screen video, recorded **without** the cursor |
| `system.wav`, `mic.wav` | Separate audio tracks |
| `webcam.mkv` | Webcam video (optional) |
| `cursor.jsonl` | Cursor position, shape and clicks per timestamp |
| `keys.jsonl` | Keystrokes per timestamp |
| `markers.json`, `notes.json` | Markers and notes added while recording |
| `project.json` | Edits, annotations, effects (the timeline) |

Because the cursor and keystrokes are stored as data, click animations, highlight halo,
smoothing, hiding and re-recording the cursor are all non-destructive edits applied at export.

## Features

### 1. Recording
- Full screen, window or region; multi-monitor
- System audio and microphone (separate tracks)
- Webcam overlay (picture-in-picture)
- Global hotkeys; pause and resume
- Record from the system tray / minimized
- Capture keystrokes
- Add markers and notes while recording
- Scheduled recording (start/stop at a set time)
- File size limit (stop or split when reached)
- Hide desktop icons during recording

### 2. Mouse cursor
- Click animations (left / right / middle, down / up / double)
- Cursor highlight halo
- Smooth mouse movement
- Hide cursor (whole video or a range)
- Re-record the cursor path over an existing recording

### 3. Editing
- Cut and trim
- Crop video
- Remove inactive sections (auto-detect idle stretches)
- Freeze frame
- Append or insert another recording
- Insert video files

### 4. Annotations
- Text boxes (with saved styles), arrows, highlight boxes, images
- Blur region
- Watermark
- On-screen keystrokes
- Pauses (exported as a held frame)
- Reusable object gallery

### 5. Effects
- Zoom and pan
- Magnifier
- Autoscroll crop (crop window follows the cursor)

### 6. Sound
- Normalize volume

### 7. Export
- MP4 (H.264 + AAC)
- GIF
- Screenshot of the current frame (PNG)

## Not included
Transitions, start/end titles, narration recording, per-clip volume, fades, noise removal,
sound clip editing, WebM/WMV/AVI/EXE export, batch export, YouTube/FTP upload,
password protection, command line, FlashBack Connect, localization,
clickable buttons (an MP4 can't contain anything clickable).

## Priorities (set 2026-11-07)

Most important: record reliably for up to 8 hours, export to MP4, and cut the start and end
(keep frames X to Y), with a choice of video and sound quality that defaults to the highest.

## Phases

| Phase | Scope | Status |
|---|---|---|
| 1. Version 1 | All of section 1 (recording); Video Editor with playback, frame selection, crop to selection, delete from start/end, undo; MP4 export with video/sound quality; PNG screenshot | **Built** |
| 2. Editor core | Delete frames from the middle, freeze frame, crop video area, remove inactive sections, append/insert recordings and videos, GIF export | Next |
| 3. Cursor effects | All of section 2 | |
| 4. Annotations | All of section 4 | |
| 5. Effects and sound | Sections 5 and 6 | |

Known limits in version 1:
- Export speed is roughly 130 frames/s at 1366×768 on this PC (Intel graphics encoder):
  an 8-hour recording takes about 1½–2 hours to export, longer at 1920×1080.
- The size limit stops the recording; splitting into several files is not built yet.
