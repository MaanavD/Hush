# Hush

> **Private, offline, free, cross-platform, open-source voice-to-text for your desktop.**
> A C#/.NET offline voice-to-text app powered by [Foundry Local](https://github.com/microsoft/foundry-local).

## Features

- **Private** — all inference runs on-device. No audio or text ever leaves your machine.
- **Offline** — works without an internet connection after the initial model download.
- **Free & open-source** — MIT-licensed. No per-token costs, no subscriptions.
- **Cross-platform** — Windows, macOS (Apple Silicon), and Linux desktop (X11). Hotkey, microphone capture, and text output all work natively on every supported OS.
- **Push-to-talk** — hold `Ctrl+Shift+H`, speak, release. Text appears in whatever field is active.
- **Live overlay** — floating window shows the current transcript while you dictate.
- **Clipboard-safe** — text is typed via `KEYEVENTF_UNICODE` / `SendInput`; your clipboard is never touched.
- **Single-instance** — only one copy of Hush can run at a time.
- **Logs to file** — diagnostic log at `~/.hush/hush.log` for easy troubleshooting.

## Prerequisites

1. **[Foundry Local](https://github.com/microsoft/foundry-local)** — must be installed before running Hush. Hush will show a clear error if it's missing.
2. **.NET 9 SDK or later** (for building from source).

## Quick Start

```bash
git clone https://github.com/maanavdalal/hush.git
cd hush

dotnet build
dotnet run --project src/Hush.App
```

On first launch Hush downloads the Nemotron streaming transcription model (~350 MB).
Progress is shown in the overlay and tray icon tooltip.

## Requirements

| Platform | Minimum                            | Audio Capture |
| -------- | ---------------------------------- | ------------- |
| Windows  | Windows 10 22H2 or later, 8 GB RAM | ✅ PortAudio  |
| macOS    | Apple Silicon, macOS 13+, 8 GB RAM | ✅ PortAudio  |
| Linux    | X11 desktop, 8 GB RAM              | ✅ PortAudio  |

## Usage

1. Launch Hush — a tray icon appears.
2. Focus the app and text field where you want to type.
3. Hold **Ctrl+H** → the overlay shows "Listening…"
4. Speak — words appear live in the overlay and are typed into your active field.
5. Release the hotkey — recording stops and the overlay disappears.

For **clean mode** (spinner, no live streaming — LLM rewrite coming soon):

- Hold **Alt+H** instead. Audio is recorded with a spinner overlay, and the full transcript is committed on release.

Right-click the tray icon to open **Settings** or **Quit**.

> **Note:** Hush cannot type into elevated (administrator) windows unless it is also run as administrator. If typing doesn't appear, check the overlay for an error message.

## CLI Remote Control

While Hush is running you can send commands to it from a terminal or script:

```bash
hush --toggle        # start/stop raw dictation (same as Ctrl+H)
hush --toggle-clean  # start/stop clean-mode dictation (same as Alt+H)
hush --cancel        # stop the active session without committing
hush --copy-last     # copy the last transcript to the clipboard
```

These flags are useful for Wayland environments (where global hotkeys may not work), stream decks, and automation scripts. They communicate with the running instance over a named pipe (Windows) or Unix socket (macOS/Linux). If Hush is not running, the command prints a message and exits with code 1.

## Configuration

Settings are stored in `~/.hush/settings.json`.

| Key                     | Default                               | Description                                                    |
| ----------------------- | ------------------------------------- | -------------------------------------------------------------- |
| `hotkey`                | `"Ctrl+H"`                            | Global push-to-talk hotkey (raw mode)                          |
| `cleanHotkey`           | `"Alt+H"`                             | Global push-to-talk hotkey (clean mode)                        |
| `postProcessingEnabled` | `true`                                | Enable LLM rewrite on clean-mode sessions (coming soon)        |
| `postProcessingModel`   | `"qwen3-0.6b"`                        | Foundry Local model alias for post-processing (coming soon)    |
| `language`              | `"en"`                                | BCP-47 transcription language                                  |
| `transcriptionModel`    | `"nemotron-speech-streaming-en-0.6b"` | Foundry Local model alias                                      |
| `overlayOpacity`        | `0.85`                                | Overlay background opacity                                     |
| `soundEffects`          | `true`                                | Start/stop audio cues                                          |
| `autoStart`             | `false`                               | Launch at OS login                                             |
| `clipboardFallback`     | `false`                               | Use clipboard paste instead of Unicode input (for legacy apps) |

## Build Prerequisites

Hush depends on pre-release NuGet packages from the Foundry Local SDK that are not yet published to nuget.org.

To build from source:

1. Obtain the required `.nupkg` files:
   - `Microsoft.AI.Foundry.Local` (version `1.0.0-dev.*`)
   - `Microsoft.AI.Foundry.Local.Core` (version `1.0.0-dev-*`)
2. Place them in the repository root directory (next to `Hush.sln`).
3. The `NuGet.config` in the repo root is already configured to resolve packages from `.` (the repo root).

> **Note:** These packages will be published to nuget.org before the stable 1.0 release, eliminating this manual step.

## Development

```bash
# Build
dotnet build -c Release

# Run tests
dotnet test -c Release

# Publish self-contained folder build (Windows)
dotnet publish src/Hush.App -p:PublishProfile=win-x64
```

## Architecture

See [SPEC.md](SPEC.md) for the full product and implementation specification.

```
Hush.App  (Avalonia UI — tray, overlay, settings)
    └── Hush.Core  (class library — engine, audio, hotkey, output)
            └── Foundry Local SDK  (on-device Nemotron streaming inference)
```

The solution also includes `Hush.ConsoleDemo`, a minimal console app for verifying mic-to-text transcription without the full UI. Useful for development and troubleshooting.

## Milestones

| #   | Milestone                            | Status                                             |
| --- | ------------------------------------ | -------------------------------------------------- |
| 0   | Project skeleton                     | ✅ Done                                            |
| 1   | Console transcription proof-of-life  | ✅ Done                                            |
| 2   | Global hotkey + live typing          | ✅ Done                                            |
| 3   | System tray + overlay UI             | ✅ Done                                            |
| 4   | Settings & polish                    | ✅ Done                                            |
| 5   | Cross-platform (macOS + Linux)       | ✅ Done (hotkeys, mic capture via PortAudio, and text output) |
| 6   | Packaging & distribution             | ✅ Done (publish profiles, single-exe)             |
| 7   | Streaming defaults + overlay refresh | ✅ Done (managed live audio via SDK 1.0.0-dev)     |

## Privacy

- No transcript history is stored by default.
- No raw audio is stored by default.
- Logs at `~/.hush/hush.log` contain timestamps and status messages only — never dictated text or audio.

## Known Limitations

- **macOS / Linux** require microphone permission on first launch. macOS will surface the standard system prompt (declared in `Info.plist` via `NSMicrophoneUsageDescription`); Linux uses ALSA via PortAudio and inherits whatever permission model your distro applies to `/dev/snd/*`.
- **Linux hotkeys require X11.** Wayland-only sessions are guarded with a startup error instead of silently failing.
- **macOS hotkeys require Accessibility permission.** Hush now reports that requirement immediately when hotkey registration fails.
- **Elevated windows** on Windows: `SendInput` is blocked by UIPI when the target app runs as administrator. Run Hush as admin to type into admin windows.
- **Code signing** is not yet set up. Windows SmartScreen or macOS Gatekeeper may show warnings on first launch.
- **Pre-release SDK dependency.** Hush depends on pre-release `Microsoft.AI.Foundry.Local` packages that are not yet published to nuget.org. See the [Build Prerequisites](#build-prerequisites) section for setup instructions.
- **No auto-update.** Check the GitHub releases page for new versions.
- All inference is on-device.

## License

[MIT](LICENSE) © 2026 Maanav Dalal
