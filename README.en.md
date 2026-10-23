[中文](README.md) ｜ **English**

# RecLite

[![CI](https://github.com/WhichWitchWin/RecLite/actions/workflows/ci.yml/badge.svg)](https://github.com/WhichWitchWin/RecLite/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/WhichWitchWin/RecLite?label=release)](https://github.com/WhichWitchWin/RecLite/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-lightgrey)](https://github.com/WhichWitchWin/RecLite/releases/latest)

A lightweight Windows audio recorder: **single-file, portable, with a built-in pre-roll buffer.**
Built to fix the "device detection" headaches of older recording software.

---

## Why it exists

Many legacy recorders (for example Moo0 AudioRecorder) still use the WINMM API from the 1990s:

- To record a microphone they require the device name to contain `Microphone`
- To record system audio they require the "Stereo Mix" virtual device

On professional audio interfaces (such as RME) this results in a "your microphone has been disabled" dialog.

RecLite uses Windows' modern **WASAPI** API instead:

- Recording voice = pick your capture device directly, whatever it happens to be called
- Recording system audio = pick any playback device and use loopback; no Stereo Mix involved

This whole class of error cannot occur.

---

## Features

**Pre-roll buffer (the core feature)**
It keeps the most recent stretch of audio buffered in the background. When you press record, that audio is prepended to the file, so you never miss "that one important sentence". The length is free-form (60, 90, 2m30s all work) and the maximum depends on the sample rate — 600 seconds at 48 kHz.

**Recording**
- Input: any capture device, or loopback of any playback device (system audio)
- Up to 2 channels; devices reporting more than 2 channels are rejected
- Recording gain 0–300% (affects only what is written to the file, not what you hear)
- Pause / resume: nothing is written and the timer is frozen while paused; resuming continues the same take
- Channel selection and pre-roll length are locked while recording — so one file never mixes two formats

**Saving**
- Formats: WAV (16 / 24 / 32-bit float), MP3 (128 / 192 / 256 / 320 kbps)
  MP3 relies on the encoder bundled with Windows; on trimmed-down systems where it is absent, only WAV is offered
- Sample rate: follow the device, or 44.1k / 48k / 88.2k / 96k
- File names: automatic timestamp, or ask on stop
- Folder: `Recordings\` next to the executable by default, changeable
- Stop modes (chosen on the spot while recording):
  `Stop` saves the take together with its pre-roll;
  `Stop (no pre-roll)` keeps only the audio after you pressed record;
  `Discard` throws this take away without saving

**Display and playback**
- Live waveform + timeline + elapsed-time indicator (white = idle / green = pre-roll buffering / yellow = recording)
- Dual level meters (input and output) with clip indicator
- Mouse wheel zooms the waveform (Shift+wheel zooms vertically); drag the waveform to seek during playback
- Playback: replay the take you just recorded, or open / drag in any audio file (WAV / MP3 / FLAC / OGG / AIFF, etc.)

**Tray**
- `Minimize to tray` tucks the window into the notification area while recording keeps running; double-click the icon to bring it back, right-click for `Show window / Quit`
- Launching `RecLite.exe` again while it is already running restores the existing window instead of starting a second copy

---

## Download

[**⬇ Download the latest `RecLite.exe`**](https://github.com/WhichWitchWin/RecLite/releases/latest/download/RecLite.exe) — a single file, just double-click it.

The release page also carries:

- `RecLite-UserGuide-zh.txt` — the detailed Chinese user guide (source: [`docs/user-guide-zh.txt`](docs/user-guide-zh.txt))
- `SHA256SUMS.txt` — checksums to verify the download

Requirements: **Windows 10 1903+ / Windows 11** (.NET Framework 4.8 ships with the OS).
Nothing else needs to be installed.

> On first launch Windows may show "Windows protected your PC". Click **More info → Run anyway**.
> The executable is not code-signed, so this is an "unknown publisher" prompt, not a virus warning.
> To stop it appearing again: right-click the exe → Properties → tick **Unblock** → OK.

Portable by design:

- Settings live in `settings.json` next to the executable; recordings go to `Recordings\` beside it
- No registry writes, no AppData writes
- Delete the folder and it is completely uninstalled

> Two things are left behind in the system temp folder. Neither is user data, and both can be deleted at any time:
> `%TEMP%\RecLite\<version>\` — the decoder library unpacked by the single-file build, about 2.5 MB, **one copy per version**;
> `%TEMP%\WPF` — WPF's own rendering temp folder.
> Native libraries cannot be loaded straight from memory, so this is the price of shipping a single file.

---

## Building from source (optional)

**Regular users do not need to build anything** — grab the binary from [Releases](https://github.com/WhichWitchWin/RecLite/releases/latest).

If you want to compile it yourself (needs Windows and .NET SDK 8):

```powershell
dotnet build -c Release src/RecLite.csproj   # output: src\bin\Release\RecLite.exe
.\scripts\build.ps1                          # or this, which always writes out\RecLite.exe
```

The result is a **single-file exe**: all managed dependencies and the native `sndfile.dll` are embedded,
resolved at runtime by `src/Core/Bundled.cs` (managed) or unpacked (native) — so only that one file needs copying.

Smoke and regression scripts: `scripts\smoke.ps1` (launches the app and checks the UI) and `scripts\verify\`.

---

## Layout

```
.github/            CI, automated release workflow, issue / PR templates
docs/               User documentation (Chinese user guide)
licenses/           Full texts of third-party licenses
scripts/            Build (build.ps1), smoke and regression scripts
src/                Source (.NET Framework 4.8 + WPF, x64)
  Audio/            Capture, encoding, pre-roll buffer, device enumeration
  Core/             Settings, logging, paths, embedded assembly loading
  UI/               Custom-drawn controls: waveform, timeline, level meters
  lib/              sndfile.dll plus the managed dependencies used for embedding
  app.ico           Application icon (16–256, multiple sizes)
out/                Local build output (not committed)
```

---

## Known limitations

- Channel selection is per device (e.g. "ADAT (3+4)"); splitting individual physical channels is not supported
- Maximum 2 channels
- MP3 supports 32 / 44.1 / 48 kHz only; other rates are converted automatically
- The pre-roll buffer costs about 23 MB per minute at 48 kHz stereo (roughly 230 MB for a 600-second buffer)
- The timeline can display up to about 4 minutes 17 seconds (a geometric limit of the screen width)

---

## Third-party components

| Component | Purpose | License |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) 2.2.1 | WASAPI capture and playback, MP3 encoding | MIT |
| [libsndfile](https://github.com/libsndfile/libsndfile) 1.2.2 | File decoding (FLAC / OGG / AIFF, etc.) | LGPL-2.1 |

Both are embedded in binary form inside `RecLite.exe`. Full license texts are in [`licenses/`](licenses/);
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the compliance details (including the LGPL
requirement that the library stay replaceable).

---

## License

This project is released under the [MIT License](LICENSE).

Third-party components carry their own licenses: NAudio is MIT, libsndfile is LGPL-2.1
(full texts in [`licenses/`](licenses/), compliance notes in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

The application icon `src/app.ico` was drawn by the project author and is released under the same MIT license.

See [CHANGELOG.md](CHANGELOG.md) for what changed between versions. Contributions are welcome — see
[CONTRIBUTING.md](CONTRIBUTING.md). For security issues, follow [SECURITY.md](SECURITY.md) instead of
opening a public issue.
