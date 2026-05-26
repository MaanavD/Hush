# Troubleshooting

Hush writes diagnostic logs to `~/.hush/hush.log`. Logs should contain status messages only, never dictated text or raw audio.

## Text does not appear in the target app

- Make sure the target text field has focus before pressing the hotkey.
- On Windows, Hush cannot type into elevated administrator windows unless Hush is also running as administrator.
- On macOS, grant Accessibility permission to Hush.
- On Linux, use an X11 session. Wayland-only sessions are not supported yet.
- Try enabling `clipboardFallback` only for legacy apps that block Unicode keystroke input.

## The microphone is unavailable

- Confirm the OS granted microphone permission.
- Open Settings and choose a specific microphone instead of the default device.
- Close other applications that may hold exclusive access to the input device.

## First launch is slow

The first launch downloads and prepares the configured Foundry Local models. Keep Hush open until the overlay/tray progress completes. Later launches use the cached model.

## Model alias not found

Foundry Local model aliases can change while the SDK is pre-release. Open Settings and choose one of the listed models, or restore the default settings file after backing up any custom prompts.

## Where to report issues

Use the bug report template and include:

- Hush version or commit SHA.
- OS version/build and install method.
- Relevant lines from `~/.hush/hush.log`.
- Whether microphone/accessibility permissions are granted.
