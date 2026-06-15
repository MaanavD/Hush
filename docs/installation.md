# Installation

Hush is distributed as self-contained release artifacts from GitHub Releases. You do not need to install the .NET runtime for packaged .NET builds.

## Windows

1. Download one of the Windows artifacts and its `.sha256` file from the release:
   - `Hush-<version>-win-x64.zip` — .NET/Avalonia app, true self-contained single exe.
   - `Hush-Rust-<version>-win-x64.zip` — Rust/Tauri app, single exe with embedded UI; requires Microsoft Edge WebView2 Runtime, which is normally already present on Windows 10/11.
2. Verify the checksum:
   ```powershell
   Get-FileHash .\Hush-<version>-win-x64.zip -Algorithm SHA256
   Get-Content .\Hush-<version>-win-x64.zip.sha256
   ```

3. Extract the zip and run `Hush.App.exe` or `hush-app.exe`.
4. If Windows SmartScreen appears for an unsigned build, confirm that the hash matches the release checksum before running.

## macOS Apple Silicon

1. Download `Hush-<version>-osx-arm64.tar.gz` and its `.sha256` file.
2. Verify the checksum:

   ```bash
   shasum -a 256 Hush-<version>-osx-arm64.tar.gz
   cat Hush-<version>-osx-arm64.tar.gz.sha256
   ```

3. Extract the archive and move `Hush.app` to `/Applications`.
4. Grant Microphone and Accessibility permissions when prompted. Accessibility is required for global hotkeys and text output.

Unsigned or ad-hoc signed builds may require Control-click > Open on first launch. Fully signed and notarized releases are the target for public distribution.

## Linux X11

1. Download `Hush-<version>-linux-x64.tar.gz` and its `.sha256` file.
2. Verify the checksum:

   ```bash
   sha256sum Hush-<version>-linux-x64.tar.gz
   cat Hush-<version>-linux-x64.tar.gz.sha256
   ```

3. Extract the archive and run `./Hush.App`.

Hush currently supports Linux desktop sessions running X11. Wayland-only sessions are not supported for global hotkeys/text output yet.

## First launch

The first launch downloads the configured Foundry Local transcription model. After that, dictation works offline unless you change to a model that has not been cached yet.
