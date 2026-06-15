# Releasing Hush

Releases are built by `.github/workflows/release.yml` from semantic-version tags.

## Release checklist

1. Confirm `CHANGELOG.md` has an entry for the release.
2. Confirm CI and CodeQL are passing on `master`.
3. Create and push a tag:

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

4. Wait for the Release workflow to produce draft release artifacts.
5. Verify each artifact checksum before publishing the draft release.

## Artifacts

| Platform | Artifact |
| --- | --- |
| Windows x64 (.NET/Avalonia) | `Hush-<version>-win-x64.zip` |
| Windows x64 (Rust/Tauri) | `Hush-Rust-<version>-win-x64.zip` |
| macOS Apple Silicon (.NET/Avalonia) | `Hush-<version>-osx-arm64.tar.gz` |
| macOS Apple Silicon (Rust/Tauri) | `Hush-Rust-<version>-osx-arm64.tar.gz` |
| Linux x64 (.NET/Avalonia) | `Hush-<version>-linux-x64.tar.gz` |
| Linux x64 (Rust/Tauri) | `Hush-Rust-<version>-linux-x64.tar.gz` |

Each artifact is published with a `.sha256` checksum.

The .NET Windows artifact is expected to contain only `Hush.App.exe`; the publish profile is self-contained and single-file, so users do not need a .NET runtime installed. Rust/Tauri artifacts contain `hush-app`/`hush-app.exe` with embedded HTML/CSS/JS. Windows uses Microsoft Edge WebView2 Runtime, macOS requires Accessibility permission for hotkeys/text output, and Linux requires WebKitGTK/X11/XTest runtime packages and an X11 session.

## Signing and notarization

Tagged public releases should be signed before publishing:

- Windows: code-sign `Hush.App.exe` and `hush-app.exe` with an Authenticode certificate before packaging.
- macOS: sign `Hush.app` with a Developer ID Application certificate and notarize it with Apple.
- Linux: checksums are required; package-manager signing can be added when distro packages exist.

The release workflow requires signing secrets for tagged Windows and macOS releases.

Windows:

- `WINDOWS_CERTIFICATE_PFX`: base64-encoded Authenticode `.pfx` certificate.
- `WINDOWS_CERTIFICATE_PASSWORD`: certificate password.
- `WINDOWS_TIMESTAMP_URL`: optional timestamp server. Defaults to `http://timestamp.digicert.com`.

macOS:

- `APPLE_CERTIFICATE_P12`: base64-encoded Developer ID Application certificate.
- `APPLE_CERTIFICATE_PASSWORD`: certificate password.
- `APPLE_DEVELOPER_ID_APPLICATION`: signing identity, for example `Developer ID Application: Example Corp (TEAMID)`.
- `APPLE_ID`: Apple ID used for notarization.
- `APPLE_TEAM_ID`: Apple Developer team ID.
- `APPLE_APP_SPECIFIC_PASSWORD`: app-specific password for notarization.

## CI/CD gates

CI validates:

- Restore/build/test on Windows, macOS, and Linux.
- E2E smoke tests that do not require real desktop automation or model downloads.
- Publish smoke checks for every release profile.

CodeQL and Dependabot provide supply-chain and static-analysis coverage for public contributions.
