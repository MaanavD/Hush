# Hush

> **Private, offline, free, cross-platform, open-source voice-to-text for your desktop.**
> A C#/.NET alternative to Wispr Flow, Superwhisper, Voibe, VoiceInk, and MacWhisper — powered by [Foundry Local](https://github.com/microsoft/foundry-local).

## Features

- **Private** — all inference runs on-device. No audio or text ever leaves your machine.
- **Offline** — works without an internet connection after the initial model download.
- **Free & open-source** — MIT-licensed. No per-token costs, no subscriptions.
- **Cross-platform** — Windows today; macOS (Apple Silicon) and Linux desktop (X11) hotkey + text output implemented, audio capture coming soon.
- **Push-to-talk** — hold `Ctrl+Shift+H`, speak, release. Text appears in whatever field is active.
- **Live overlay** — floating window shows interim transcription in real-time.
- **Clipboard-safe** — text is typed via `KEYEVENTF_UNICODE` / `SendInput`; your clipboard is never touched.
- **Single-instance** — only one copy of Hush can run at a time.
- **Logs to file** — diagnostic log at `~/.hush/hush.log` for easy troubleshooting.

## Prerequisites

1. **[Foundry Local](https://github.com/microsoft/foundry-local)** — must be installed before running Hush. Hush will show a clear error if it's missing.
2. **.NET 8 SDK or later** (for building from source).

## Quick Start

```bash
git clone https://github.com/maanavdalal/hush.git
cd hush

dotnet build
dotnet run --project src/Hush.App
```

On first launch Hush downloads the Whisper Tiny transcription model (~75 MB).
Progress is shown in the overlay and tray icon tooltip.

## Requirements

| Platform | Minimum                            | Audio Capture |
| -------- | ---------------------------------- | ------------- |
| Windows  | Windows 10 22H2 or later, 8 GB RAM | ✅ NAudio     |
| macOS    | Apple Silicon, macOS 13+, 8 GB RAM | 🔲 Planned    |
| Linux    | X11 desktop, 8 GB RAM              | 🔲 Planned    |

## Usage

1. Launch Hush — a tray icon appears.
2. Focus the app and text field where you want to type.
3. Hold **Ctrl+Shift+H** → the overlay shows "Listening…"
4. Speak — words appear live in the overlay and are typed into your active field.
5. Release the hotkey — recording stops and the overlay disappears.

Right-click the tray icon to open **Settings** or **Quit**.

> **Note:** Hush cannot type into elevated (administrator) windows unless it is also run as administrator. If typing doesn't appear, check the overlay for an error message.

## Configuration

Settings are stored in `~/.hush/settings.json`.

| Key                  | Default          | Description                                                    |
| -------------------- | ---------------- | -------------------------------------------------------------- |
| `hotkey`             | `"Ctrl+Shift+H"` | Global push-to-talk hotkey                                     |
| `language`           | `"en"`           | BCP-47 transcription language                                  |
| `transcriptionModel` | `"whisper-tiny"` | Foundry Local model alias                                      |
| `overlayOpacity`     | `0.85`           | Overlay background opacity                                     |
| `soundEffects`       | `true`           | Start/stop audio cues                                          |
| `autoStart`          | `false`          | Launch at OS login                                             |
| `clipboardFallback`  | `false`          | Use clipboard paste instead of Unicode input (for legacy apps) |

## Development

```bash
# Build
dotnet build -c Release

# Run tests
dotnet test -c Release

# Publish self-contained single-exe (Windows)
dotnet publish src/Hush.App -p:PublishProfile=win-x64
```

## Architecture

See [SPEC.md](SPEC.md) for the full product and implementation specification.

```
Hush.App  (Avalonia UI — tray, overlay, settings)
    └── Hush.Core  (class library — engine, audio, hotkey, output)
            └── Foundry Local SDK  (on-device Whisper inference)
```

## Milestones

| #   | Milestone                           | Status                                             |
| --- | ----------------------------------- | -------------------------------------------------- |
| 0   | Project skeleton                    | ✅ Done                                            |
| 1   | Console transcription proof-of-life | ✅ Done                                            |
| 2   | Global hotkey + live typing         | ✅ Done                                            |
| 3   | System tray + overlay UI            | ✅ Done                                            |
| 4   | Settings & polish                   | ✅ Done                                            |
| 5   | Cross-platform (macOS + Linux)      | ✅ Done (hotkeys + text output; audio capture TBD) |
| 6   | Packaging & distribution            | ✅ Done (publish profiles, single-exe)             |

## Privacy

- No transcript history is stored by default.
- No raw audio is stored by default.
- Logs at `~/.hush/hush.log` contain timestamps and status messages only — never dictated text or audio.

## Known Limitations

- **macOS / Linux audio capture** is not yet implemented. Hotkeys and text output work, but microphone capture requires a platform-specific backend (planned).
- **Elevated windows** on Windows: `SendInput` is blocked by UIPI when the target app runs as administrator. Run Hush as admin to type into admin windows.
- **Code signing** is not yet set up. Windows SmartScreen or macOS Gatekeeper may show warnings on first launch.
- **No auto-update.** Check the GitHub releases page for new versions.
- All inference is on-device.

## License

[MIT](LICENSE) © 2026 Maanav Dalal
