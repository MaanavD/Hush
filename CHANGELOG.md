# Changelog

All notable changes to Hush will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Hush uses [semantic versioning](https://semver.org/).

---

## [Unreleased]

### Added

- Public open-source readiness pass: install, usage, troubleshooting, and release-maintainer docs.
- CodeQL workflow and Dependabot configuration for supply-chain and static-analysis coverage.
- Cross-platform CI matrix that validates restore, build, tests, E2E smoke coverage, and publish profiles on Windows, macOS, and Linux.
- Release preflight, checksum validation, pinned third-party actions, and signing/notarization hooks for release automation.
- **Cross-platform microphone capture** via [PortAudioSharp2](https://www.nuget.org/packages/PortAudioSharp2/) — replaces the Windows-only NAudio capture path. PortAudio ships pre-built native libraries for `win-x64`, `osx-arm64`/`osx-x64`, and `linux-x64`/`linux-arm64` inside the NuGet, so no extra system packages are required on Windows or macOS. Linux relies on the system's ALSA library (preinstalled on virtually every desktop distro)
- Windows application icon (`hush-icon.ico`) embedded in `Hush.App.exe`; shown in File Explorer, taskbar, and Alt-Tab
- Windows application manifest: Per-Monitor V2 DPI awareness, UTF-8 active code page, long-path aware, Windows 7/8/10/11 `supportedOS` declarations, `asInvoker` execution level (no UAC prompt)
- Exe details populated: `FileDescription`, `ProductName`, `FileVersion`, `ProductVersion`, `CompanyName`, `LegalCopyright`
- `Settings` window now uses the Hush icon in its title bar and taskbar entry
- GitHub Actions release workflow (`.github/workflows/release.yml`): on `v*` tag push, builds the single-exe `win-x64` bundle, zips it with an SHA-256 checksum, and attaches both to a draft GitHub Release
- Release workflow extended with **macOS arm64** and **Linux x64** jobs that build natively on GitHub-hosted runners. The macOS job assembles a `Hush.app` bundle (Info.plist + ad-hoc-signed Mach-O + `.icns`) and produces `Hush-<version>-osx-arm64.tar.gz`; the Linux job produces `Hush-<version>-linux-x64.tar.gz`

### Changed

- Centralized shared assembly/release metadata in `Directory.Build.props`.
- Updated public docs and issue templates for the current default hotkeys: `Ctrl+H` and `Ctrl+Alt+H`.
- `AudioCaptureService` now uses PortAudio process-wide (one-time `Pa_Initialize`, never terminated) instead of NAudio's `WaveInEvent`. Same 16 kHz / 16-bit / mono / ~50 ms PCM contract; `IAudioCaptureService` interface and `AudioLevelChanged` event are unchanged
- NAudio is now a Windows-only dependency, scoped to `SoundEffectService` (start/stop chimes)
- `SettingsViewModel` populates the microphone list lazily when the settings window opens, instead of in its constructor — avoids loading native audio libs during process startup or unit tests

## [1.0.0] — 2026-04-09

### Added

- Private, offline, cross-platform voice-to-text powered by Foundry Local (Nemotron ASR, streaming CPU)
- Push-to-talk global hotkey (`Ctrl+Shift+H`) with live floating overlay
- Streaming commit mode — stable transcription deltas are typed progressively while speaking
- Spinner mode — animated indicator typed in the active field; full transcript appears on key release
- Waveform visualiser in the overlay listening pill
- System tray icon with Settings and Quit actions
- Settings window: hotkey, language, microphone selector, model alias, sound effects, streaming toggle, launch-at-login, overlay opacity
- Clipboard-safe text output via `KEYEVENTF_UNICODE` / `SendInput` (Windows) and `xdotool` (Linux X11)
- macOS hotkey registration and text output via `CGEventPost` (audio capture planned)
- Single-instance enforcement via named mutex
- File-based diagnostic log at `~/.hush/hush.log` (never records dictated text)
- Self-contained Windows publish profile — ships as a single `.exe`; model is downloaded on first run by the Foundry Local SDK
- 18 xUnit test files covering core services, settings, session orchestration, and edge cases
- MIT license

[Unreleased]: https://github.com/MaanavD/Hush/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/MaanavD/Hush/releases/tag/v1.0.0
