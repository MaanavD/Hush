# Contributing to Hush

Thank you for your interest in contributing! Hush is a small, focused project and every contribution — bug reports, feature suggestions, documentation fixes, or code — is welcome.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [How to Contribute](#how-to-contribute)
  - [Reporting Bugs](#reporting-bugs)
  - [Suggesting Features](#suggesting-features)
  - [Submitting Pull Requests](#submitting-pull-requests)
- [Development Setup](#development-setup)
- [Project Structure](#project-structure)
- [Coding Conventions](#coding-conventions)
- [Running Tests](#running-tests)
- [Release and CI Expectations](#release-and-ci-expectations)

---

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating you agree to uphold it. Please report unacceptable behaviour to the maintainer.

---

## Getting Started

1. **Fork** the repository and clone your fork.
2. Follow the [Build Prerequisites](README.md#build-prerequisites) section of the README to obtain the pre-release NuGet packages.
3. Run `dotnet build` to verify your environment.
4. Run `dotnet test` to confirm all tests pass.

---

## How to Contribute

### First Contributions

If you are new to Hush, start with issues labeled `good first issue`, `documentation`, or `help wanted`. Docs-only improvements, clearer troubleshooting steps, small test cases, and platform-specific install notes are all valuable.

Before starting a larger feature, open or comment on an issue so maintainers can confirm scope and avoid duplicate work.

### Reporting Bugs

Use the [Bug Report](https://github.com/MaanavD/Hush/issues/new?template=bug_report.yml) issue template. The more detail you provide — platform, steps to reproduce, and log output — the faster the fix will land.

### Suggesting Features

Use the [Feature Request](https://github.com/MaanavD/Hush/issues/new?template=feature_request.yml) issue template. For large changes it is worth opening a discussion first to align on approach before writing code.

### Submitting Pull Requests

1. Open an issue (or find an existing one) so we can discuss the approach.
2. Create a feature branch from `master`:
   ```bash
   git switch -c feat/my-short-description
   ```
3. Make your changes and add tests where appropriate.
4. Ensure `dotnet test -c Release` passes locally.
5. Push your branch and open a pull request against `master`.
6. Fill in the PR template — link to the related issue, summarize changes, and describe how you tested.

> **Tip:** Keep pull requests small and focused. A PR that does one thing is much easier to review than a PR that does many things.

Branch names should be short and descriptive, for example `fix/linux-hotkey-error`, `docs/release-checks`, or `feat/custom-prompts`. Commit messages do not need to follow a strict convention, but imperative summaries such as `Fix settings validation for duplicate hotkeys` are easiest to review.

---

## Development Setup

### Prerequisites

| Tool                                                        | Version                                                           |
| ----------------------------------------------------------- | ----------------------------------------------------------------- |
| [.NET SDK](https://dotnet.microsoft.com/download)           | 9.0 or later                                                      |
| Foundry Local SDK packages                                  | Restored through `NuGet.config` from nuget.org and ORT-Nightly    |

### Build

```bash
dotnet build
```

### Run (Windows)

```bash
dotnet run --project src/Hush.App
```

### Run the console demo (useful for verifying mic-to-text without the UI)

```bash
dotnet run --project src/Hush.ConsoleDemo
```

---

## Project Structure

```
src/
  Hush.App/        # Avalonia UI — tray, overlay, settings windows
  Hush.Core/       # Class library — engine, audio, hotkey, output, settings
  Hush.ConsoleDemo/# Minimal console app for testing transcription

tests/
  Hush.Core.Tests/ # xUnit unit tests for Hush.Core
  Hush.App.Tests/  # xUnit tests for app-level logic
  Hush.E2E.Tests/  # xUnit E2E smoke tests and opt-in desktop/model tests
```

For a deeper explanation of every component see [SPEC.md](SPEC.md).

---

## Coding Conventions

- **C# 13 / .NET 9** language features are welcome.
- Follow the style of the surrounding code — naming, spacing, and XML doc comments.
- All source files should start with the copyright header:
  ```csharp
  // Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.
  ```
- Public API members should have `<summary>` XML doc comments.
- Prefer `ILogger<T>` injection over `Console.WriteLine` for diagnostic output.
- Privacy-first: never log dictated text, audio content, or personally identifying information.

---

## Running Tests

```bash
dotnet test -c Release
```

Tests use xUnit and Moq. Unit tests do not require real hardware; platform services are mocked. The E2E project includes safe smoke tests that run in CI plus opt-in suites for real desktop apps and model-backed audio.

To collect code coverage:

```bash
dotnet test -c Release --collect:"XPlat Code Coverage"
```

### Opt-in E2E suites

These are intentionally disabled by default because they need a local desktop session, model downloads, or OS-specific permissions:

| Suite | Enable with | Notes |
| --- | --- | --- |
| Model-backed audio transcription | `HUSH_RUN_AUDIO_E2E=1` | Downloads/loads the configured Foundry Local transcription model |
| Windows Notepad E2E | `HUSH_RUN_NOTEPAD_E2E=1` | Requires unlocked Windows desktop session |
| macOS TextEdit E2E | `HUSH_RUN_MACOS_E2E=1` | Requires macOS Accessibility and microphone permissions |

## Release and CI Expectations

- Every PR should pass the CI workflow, which builds, tests, runs E2E smoke tests, and publish-smoke-validates Windows, macOS, and Linux artifacts.
- Security scanning runs through CodeQL and Dependabot.
- Release tags must be semantic versions in the form `vMAJOR.MINOR.PATCH` such as `v1.0.0`.
- Maintainer release steps, signing requirements, and artifact verification are documented in [docs/releasing.md](docs/releasing.md).

---

## Questions?

Open a [Discussion](https://github.com/MaanavD/Hush/discussions) rather than an issue for general questions. Issues are reserved for confirmed bugs and actionable feature requests.
