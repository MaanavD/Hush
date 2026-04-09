# Changelog

All notable changes to Hush will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Hush uses [semantic versioning](https://semver.org/).

---

## [Unreleased]

## [1.0.0] — 2026-04-09

### Added
- Private, offline, cross-platform voice-to-text powered by Foundry Local (Whisper / Nemotron CPU int4)
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
- Self-contained Windows publish profile (single `.exe`)
- `dist/setup.ps1` — one-command model download and first-launch helper for end users
- 18 xUnit test files covering core services, settings, session orchestration, and edge cases
- MIT license

[Unreleased]: https://github.com/maanavdalal/hush/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/maanavdalal/hush/releases/tag/v1.0.0
