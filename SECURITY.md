# Security Policy

## Supported Versions

Only the latest release of Hush receives security fixes.

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Instead, email **security@[your-domain]** or use [GitHub private vulnerability reporting](https://github.com/maanavdalal/hush/security/advisories/new).

Include:
- A description of the vulnerability and its potential impact
- Steps to reproduce or a proof-of-concept
- The version(s) affected

You will receive an acknowledgement within 48 hours and a resolution timeline within 7 days.

## Scope

Hush is a local, offline desktop app. All inference runs on-device. There is no server, no API, and no network communication during normal use (the model download in `setup.ps1` is the only outbound request and goes to HuggingFace and Microsoft CDNs).

Security issues most relevant to this project:
- Malicious input causing code execution via the hotkey or dictation pipeline
- Path traversal or arbitrary file write in the settings or model cache paths
- Privilege escalation via the Windows `SendInput` / UIPI bypass path
- Supply-chain issues in NuGet dependencies

## Out of Scope

- Denial-of-service against the local process (no network surface)
- Issues requiring physical access to the machine
- Vulnerabilities in third-party dependencies (report those upstream)
