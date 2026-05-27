# Usage

Launch Hush, focus the text field where you want dictation to appear, then use one of the global hotkeys.

| Mode | Default hotkey | Behavior |
| --- | --- | --- |
| Raw dictation | `Ctrl+H` | Streams stable transcript segments into the focused app while you speak |
| Clean mode | `Ctrl+Alt+H` | Records while held, rewrites locally, then commits the cleaned text on release |

Right-click the tray icon for Settings and Quit.

## Settings

Settings are stored at `~/.hush/settings.json`.

Common settings:

- `hotkey`: raw dictation hotkey.
- `cleanHotkey`: clean-mode hotkey.
- `language`: transcription language hint.
- `transcriptionModel`: Foundry Local transcription model alias.
- `postProcessingEnabled`: enables the local rewrite pass for clean mode.
- `postProcessingModel`: Foundry Local language model alias for clean mode.
- `overlayOpacity`: overlay background opacity.
- `clipboardFallback`: opt-in fallback for legacy apps that block Unicode keystroke injection.

## CLI remote control

When Hush is already running, scripts can control it with:

```bash
hush --toggle
hush --toggle-clean
hush --cancel
hush --copy-last
```

These commands communicate with the running desktop process through a named pipe on Windows or a Unix socket on macOS/Linux.

## Permissions

- Windows: run Hush as administrator only if you need to type into elevated administrator windows.
- macOS: grant Microphone and Accessibility permissions.
- Linux: use an X11 desktop session and ensure the user can access the audio device.
