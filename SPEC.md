# Hush — Product & Implementation Spec

> **Private, offline, free, cross-platform, open-source voice-to-text for your desktop.**
> A C#/.NET alternative to Wispr Flow, Superwhisper, Voibe, VoiceInk, and MacWhisper — powered by [Foundry Local](https://github.com/microsoft/foundry-local).

---

## 1  Vision & Principles

| Principle | Detail |
|-----------|--------|
| **Private** | All inference runs on-device. No audio or text ever leaves the machine. |
| **Offline** | Works without an internet connection (after initial model download). |
| **Free & OSS** | MIT-licensed. No per-token costs, no subscriptions. |
| **Cross-platform** | Day-one release target: Windows, macOS (Apple Silicon), and Linux desktop on X11 — single codebase via Avalonia UI + .NET 9. Wayland is best-effort until user demand and test coverage justify full support. |
| **Simple** | System-tray app with a single hotkey. Zero configuration required on supported hardware after model download and OS permissions are granted. |
| **Low-latency** | Text appears live in the overlay and committed text is typed into the focused app with near-live latency. |

---

## 2  Core User Flow

```
1. User launches Hush → system tray icon appears. If the model is not cached yet, Hush downloads it with visible progress and prepares it in the background.
2. User focuses the target app and text field, then holds the global hotkey (default: Ctrl+Shift+H) → small overlay shows "Listening…"
3. User speaks → transcription updates stream in real-time.
4. The overlay shows live interim text immediately. Hush types committed text segments into the currently focused app via simulated keystrokes with near-live latency.
5. User releases hotkey → recording stops, final committed text flushes, overlay disappears.
```

**Why simulated keystrokes instead of clipboard paste?**
- Preserves the user's clipboard contents — no surprise overwrites.
- Text appears as you speak, not all at once after release — lower perceived latency.
- Works identically to physical typing from the target app's perspective.

### 2.1  MVP Support Matrix

- **Windows**: supported on day one.
- **macOS (Apple Silicon)**: supported on day one.
- **Linux desktop (X11)**: supported on day one.
- **Linux Wayland**: explicitly not a release blocker for v1. Treat as best-effort until there is enough user demand and reliable test coverage.
- **Hardware baseline**: target machines with at least 8 GB RAM and hardware roughly from the last 5 years that can run the chosen Nemotron transcription model at acceptable latency.
- **Packaging policy**: end users should install a self-contained executable or simple installer. External command-line tools such as `xdotool` or `wtype` are acceptable for development spikes, but not as required end-user prerequisites for MVP.

### 2.2  Focus, Streaming, and Output Rules

- The overlay must be non-activating and must never steal keyboard focus.
- Hush types into whichever app and field currently owns OS keyboard focus. In the normal flow this is the field the user selected before pressing the hotkey.
- If focus changes mid-session, subsequent committed text is sent to the new focus target.
- Assume the transcription SDK may emit unstable interim hypotheses before final or committed text. The architecture must handle both unstable partials and committed output without redesign.
- For MVP, interim text is shown in the overlay only. Only committed deltas are typed into the target app. This avoids destructive backspacing or text rewrites in the user's active field while still feeling live.
- Any future "type unstable partials into the app" mode should be opt-in and must include correction logic, undo safety, and a clear user-facing warning.

### 2.3  Privacy and Diagnostics Rules

- No transcript history is stored by default.
- No raw audio is stored by default.
- Logs must never contain dictated text, raw audio, or reconstructed transcript content.
- Crash and diagnostic logs may include app state, hardware capability info, active backend selection, model load progress, and error codes, but not user content.
- Privacy defaults are non-negotiable. Any future diagnostic mode that captures user content must be explicit opt-in and disabled by default.

---

## 3  Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│                        Hush.App (Avalonia)                   │
│  ┌──────────┐  ┌──────────────┐  ┌─────────────────────┐    │
│  │ Tray Icon │  │ Overlay View │  │ Settings Window     │    │
│  └─────┬────┘  └──────┬───────┘  └──────────┬──────────┘    │
│        │               │                     │               │
│  ┌─────▼───────────────▼─────────────────────▼──────────┐    │
│  │              Hush.Core  (class library)              │    │
│  │  ┌─────────────┐ ┌──────────────┐ ┌──────────────┐  │    │
│  │  │ HotkeyService│ │ AudioCapture │ │ KeystrokeTypr│  │    │
│  │  └──────┬──────┘ └──────┬───────┘ └──────┬───────┘  │    │
│  │         │               │                │           │    │
│  │  ┌──────▼───────────────▼────────────────▼───────┐   │    │
│  │  │          TranscriptionEngine                  │   │    │
│  │  │  FoundryLocalManager → Nemotron model         │   │    │
│  │  │  LiveAudioTranscriptionSession                │   │    │
│  │  └───────────────────────────────────────────────┘   │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │       Foundry Local SDK (NuGet)                      │    │
│  │  Microsoft.AI.Foundry.Local / .WinML (Windows)       │    │
│  │  ↕ Native AOT Core (ONNX Runtime + GenAI)            │    │
│  └──────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────┘
```

---

## 4  Solution Structure

```
Hush/
├── Hush.sln
├── src/
│   ├── Hush.Core/                    # Class library — no UI dependency
│   │   ├── Hush.Core.csproj
│   │   ├── Audio/
│   │   │   ├── AudioCaptureService.cs        # Mic capture via backend abstraction (16kHz 16-bit mono PCM)
│   │   │   └── IAudioCaptureService.cs
│   │   ├── Transcription/
│   │   │   ├── TranscriptionEngine.cs        # Wraps Foundry Local live transcription
│   │   │   ├── ITranscriptionEngine.cs
│   │   │   └── TranscriptionResult.cs        # DisplayText, CommittedDelta, IsFinal, StartTime, EndTime
│   │   ├── Input/
│   │   │   ├── IGlobalHotkeyService.cs
│   │   │   └── GlobalHotkeyService.cs        # Per-platform hotkey registration
│   │   ├── Output/
│   │   │   ├── ITextOutputService.cs
│   │   │   └── KeystrokeTypingService.cs     # Simulate keystrokes to type text into focused app
│   │   ├── Models/
│   │   │   ├── IModelManager.cs
│   │   │   └── ModelManager.cs               # Download/load/unload Foundry models
│   │   ├── Configuration/
│   │   │   └── HushSettings.cs               # User preferences (hotkey, language, etc.)
│   │   │   └── SettingsService.cs            # Load/save JSON settings
│   │   └── Session/
│   │       ├── DictationSession.cs           # Orchestrates: hotkey → capture → transcribe → output
│   │       └── IDictationSession.cs
│   │
│   └── Hush.App/                     # Avalonia desktop app
│       ├── Hush.App.csproj
│       ├── App.axaml / App.axaml.cs
│       ├── Program.cs
│       ├── ViewModels/
│       │   ├── MainViewModel.cs              # App-level state (model status)
│       │   ├── OverlayViewModel.cs           # Live transcription text, recording state
│       │   └── SettingsViewModel.cs          # Settings bindings
│       ├── Views/
│       │   ├── OverlayWindow.axaml           # Floating translucent overlay during dictation
│       │   ├── SettingsWindow.axaml          # Configuration UI
│       │   └── TrayIcon.cs                   # System tray icon + context menu
│       ├── Assets/
│       │   ├── hush-icon.ico
│       │   └── hush-icon.png
│       └── Platforms/
│           ├── Windows/
│           │   └── WindowsHotkeyProvider.cs  # Win32 RegisterHotKey
│           ├── macOS/
│           │   └── MacHotkeyProvider.cs      # CGEvent tap
│           └── Linux/
│               └── LinuxHotkeyProvider.cs    # X11/XGrab or libkeybinder
│
├── tests/
│   ├── Hush.Core.Tests/
│   │   └── Hush.Core.Tests.csproj
│   └── Hush.App.Tests/
│       └── Hush.App.Tests.csproj
│
├── .github/                          # (CI workflows — planned)
│
├── README.md
├── LICENSE                           # MIT
├── SPEC.md                           # This file
└── .editorconfig
```

---

## 5  Technology Stack

| Layer | Technology | Notes |
|-------|-----------|-------|
| **Runtime** | .NET 9 | LTS, cross-platform, AOT-capable |
| **UI** | Avalonia UI 11 | Cross-platform XAML. System tray, overlays, transparency support. |
| **AI Inference** | Foundry Local C# SDK | `Microsoft.AI.Foundry.Local` (unified managed SDK, cross-platform) |
| **Speech Model** | Nemotron | Live streaming transcription via `LiveAudioTranscriptionSession`; treat stream results as potentially unstable until committed. |
| **Audio Capture** | Backend abstraction | Windows can use NAudio; macOS/Linux may use a different backend if NAudio is not reliable enough. The app architecture should hide this behind a common interface. |
| **Keystroke Simulation** | Per-platform native implementation | `SendInput` (Win), `CGEventPost` (macOS), bundled native helper/P/Invoke on Linux. Avoid requiring separate end-user utilities when packaging MVP. |
| **Hotkey** | Per-platform P/Invoke | `RegisterHotKey` (Win), `CGEventTap` (macOS), `XGrabKey` (Linux) |
| **Settings** | `System.Text.Json` | JSON file in `~/.hush/settings.json` |
| **Logging** | `Microsoft.Extensions.Logging` | Console + file sinks with strict no-transcript logging rules |
| **Testing** | xUnit + Moq | Unit tests for Core; integration tests for engine |
| **CI** | GitHub Actions | Matrix: `windows-latest`, `macos-latest`, `ubuntu-latest` |
| **Packaging** | `dotnet publish` self-contained | Self-contained executables/installers with bundled native dependencies where licensing permits |

---

## 6  Key Components — Detailed Design

### 6.1  `TranscriptionEngine`

The core of Hush. Wraps Foundry Local's `LiveAudioTranscriptionSession`.

```csharp
public class TranscriptionEngine : ITranscriptionEngine, IAsyncDisposable
{
    private FoundryLocalManager _manager;
    private Model _model;
    private OpenAIAudioClient _audioClient;
    private LiveAudioTranscriptionSession? _session;

    public async Task InitializeAsync(string modelAlias = "nemotron")
    {
        await FoundryLocalManager.CreateAsync(
            new Configuration { AppName = "Hush" },
            NullLogger.Instance);
        _manager = FoundryLocalManager.Instance;
        
        // On Windows, download hardware acceleration EPs
        if (OperatingSystem.IsWindows())
            await _manager.DownloadAndRegisterEpsAsync();
        
        var catalog = await _manager.GetCatalogAsync();
        _model = await catalog.GetModelAsync(modelAlias)
            ?? throw new InvalidOperationException($"Model '{modelAlias}' not found");
        
        await _model.DownloadAsync();   // No-op if already cached
        await _model.LoadAsync();
        _audioClient = await _model.GetAudioClientAsync();
    }

    public async Task<LiveAudioTranscriptionSession> StartSessionAsync(
        int sampleRate = 16000, int channels = 1, string language = "en")
    {
        _session = _audioClient.CreateLiveTranscriptionSession();
        _session.Settings.SampleRate = sampleRate;
        _session.Settings.Channels = channels;
        _session.Settings.Language = language;
        await _session.StartAsync();
        return _session;
    }

    public async Task StopSessionAsync()
    {
        if (_session != null)
            await _session.StopAsync();
        _session = null;
    }
}
```

Design constraints for the engine:

- Treat SDK stream events as potentially unstable interim text unless the Foundry Local contract proves they are append-only committed segments.
- Normalize SDK output into two streams: `DisplayText` for the overlay and `CommittedDelta` for keystroke output.
- Keep the SDK-specific event shape behind the engine boundary so the rest of the app does not depend on whether Foundry Local emits partials, finals, or both.

### 6.2  `AudioCaptureService`

Captures microphone audio and feeds it to the transcription session. The service contract is cross-platform; the concrete backend can differ per OS.

```csharp
public class AudioCaptureService : IAudioCaptureService, IDisposable
{
    private WaveInEvent? _waveIn;
    private LiveAudioTranscriptionSession? _session;

    public void Start(LiveAudioTranscriptionSession session)
    {
        _session = session;
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(rate: 16000, bits: 16, channels: 1),
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
                _ = _session.AppendAsync(
                    new ReadOnlyMemory<byte>(e.Buffer, 0, e.BytesRecorded));
        };

        _waveIn.StartRecording();
    }

    public void Stop()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
    }
}
```

### 6.3  `DictationSession` (Orchestrator)

Coordinates the full dictation lifecycle. Key design: the overlay updates from interim results immediately, while only committed text is typed into the focused app.

```csharp
public class DictationSession : IDictationSession
{
    private readonly ITranscriptionEngine _engine;
    private readonly IAudioCaptureService _capture;
    private readonly ITextOutputService _output;  // KeystrokeTypingService

    public event Action<string>? OnInterimText;     // UI overlay shows unstable live text
    public event Action<string>? OnTextChunk;       // UI can show what was committed and typed
    public event Action? OnSessionStopped;

    private Task? _transcriptionLoop;

    public async Task StartAsync()
    {
        var session = await _engine.StartSessionAsync();
        _capture.Start(session);

        // Background task: update overlay immediately and only type committed deltas
        _transcriptionLoop = Task.Run(async () =>
        {
            await foreach (var result in session.GetTranscriptionStream())
            {
                if (!string.IsNullOrEmpty(result.DisplayText))
                    OnInterimText?.Invoke(result.DisplayText);

                if (!string.IsNullOrEmpty(result.CommittedDelta))
                {
                    await _output.TypeTextAsync(result.CommittedDelta);
                    OnTextChunk?.Invoke(result.CommittedDelta);
                }
            }
        });
    }

    public async Task StopAsync()
    {
        _capture.Stop();
        await _engine.StopSessionAsync();
        if (_transcriptionLoop != null)
            await _transcriptionLoop;  // Wait for final chunks to be typed
        OnSessionStopped?.Invoke();
    }
}
```

### 6.4  `KeystrokeTypingService`

Simulates keystrokes to type text into whatever app is currently focused. Never touches the clipboard.

```csharp
public class KeystrokeTypingService : ITextOutputService
{
    /// <summary>
    /// Types the given text into the focused application by simulating
    /// individual key presses. Runs on a dedicated STA thread (Windows)
    /// or equivalent mechanism per platform.
    /// </summary>
    public Task TypeTextAsync(string text)
    {
        if (OperatingSystem.IsWindows())
            return WindowsKeystrokeTyper.TypeAsync(text);
        else if (OperatingSystem.IsMacOS())
            return MacKeystrokeTyper.TypeAsync(text);
        else
            return LinuxKeystrokeTyper.TypeAsync(text);
    }
}

// --- Windows implementation (user32.dll SendInput) ---
internal static class WindowsKeystrokeTyper
{
    public static Task TypeAsync(string text)
    {
        // Use INPUT structs with KEYBDINPUT for each Unicode character.
        // SendInput sends KEYEVENTF_UNICODE so any UTF-16 char works
        // without needing to map to virtual key codes.
        var inputs = new INPUT[text.Length * 2]; // key-down + key-up per char
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = MakeUnicodeKeyDown(text[i]);
            inputs[i * 2 + 1] = MakeUnicodeKeyUp(text[i]);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return Task.CompletedTask;
    }
}

// --- macOS implementation (CGEventPost) ---
// Uses CGEventCreateKeyboardEvent + CGEventKeyboardSetUnicodeString
// Posts to kCGHIDEventTap for the active app.

// --- Linux implementation (bundled native helper / libxdo) ---
// Prefer a bundled helper or libxdo via P/Invoke so end users do not
// need to separately install xdotool or other CLI tools.
```

**Why `KEYEVENTF_UNICODE` / `SendInput`?**
- Types arbitrary Unicode without virtual key code mapping.
- Works in any app — text editors, browsers, terminals, IDEs.
- Each committed delta is a small burst of keystrokes, so latency is low without rewriting previously typed text.
- The user's clipboard is never touched.

MVP policy for text output:
- Type only committed deltas into the target app.
- Do not issue corrective backspaces into the user's app for interim ASR rewrites in MVP.
- Do not require elevated privileges to support normal text fields; if a target blocks injection because of OS or app security boundaries, fail gracefully and surface a clear warning.

### 6.5  `OverlayWindow`

A small, floating, semi-transparent window that appears during dictation.

```
┌─────────────────────────────────┐
│  🎙  Listening...               │
│                                 │
│  "the quick brown fox jumped…"  │
│                                 │
│  [Ctrl+Shift+H to stop]    │
└─────────────────────────────────┘
```

- Always-on-top, no taskbar entry, no focus steal
- Non-activating / focus-preserving by design
- Rounded corners, semi-transparent background (blur if platform supports)
- Shows streaming transcription text in real-time, including unstable interim text
- Positioned near cursor or bottom-center of screen
- Disappears when hotkey is released

---

## 7  Implementation Milestones

### Milestone 0: Project Skeleton (Day 1)
- [ ] Create solution with `Hush.Core` and `Hush.App` projects
- [ ] Add NuGet references: Foundry Local SDK, NAudio, Avalonia
- [ ] Set up conditional PackageReference for WinML vs cross-platform SDK
- [ ] Define backend abstractions up front for audio capture, hotkeys, and keystroke injection
- [ ] Create `.editorconfig`, `LICENSE`, `README.md`
- [ ] Basic CI pipeline (build on 3 platforms)

### Milestone 1: Proof of Life — Console Transcription (Day 1-2)
- [ ] Implement `TranscriptionEngine` — initialize Foundry Local, load Nemotron
- [ ] Implement `AudioCaptureService` — prove one working capture backend per target OS
- [ ] Wire them together in a simple console `Program.cs`
- [ ] Validate whether the SDK emits unstable partials, committed deltas, or both
- [ ] Verify: speak into mic → see text in terminal
- [ ] **Exit criteria:** Real-time text appears in console from mic input

### Milestone 2: Global Hotkey + Live Typing (Day 2-3)
- [ ] Implement `GlobalHotkeyService` for Windows (Win32 `RegisterHotKey`)
- [ ] Implement `KeystrokeTypingService` (Windows `SendInput` with `KEYEVENTF_UNICODE`)
- [ ] Implement `DictationSession` orchestrator — show interim text live and type committed deltas
- [ ] Verify: hold hotkey → speak → words appear live in Notepad as you talk
- [ ] **Exit criteria:** Push-to-talk → live-typed text in focused app on Windows

### Milestone 3: System Tray + Overlay UI (Day 3-5)
- [ ] Create Avalonia app with system tray icon
- [ ] Build `OverlayWindow` — floating, transparent, always-on-top
- [ ] Bind overlay to `DictationSession` events for live text preview
- [ ] Tray context menu: Start/Stop, Settings, Quit
- [ ] Show model download progress on first launch
- [ ] **Exit criteria:** Polished tray app with visual dictation feedback

### Milestone 4: Settings & Polish (Day 4-5)
- [ ] Settings window: hotkey, language, model, auto-start
- [ ] Persist settings to `~/.hush/settings.json`
- [ ] Auto-start on login (optional)
- [ ] Sound effects: start/stop beeps
- [ ] Error handling: model not found, mic not available, etc.
- [ ] **Exit criteria:** App is configurable and handles errors gracefully

### Milestone 5: Cross-Platform (Day 5-8)
- [ ] macOS hotkey provider (`CGEventTap` via P/Invoke / ObjCRuntime)
- [ ] macOS keystroke typing (`CGEventCreateKeyboardEvent` + `CGEventKeyboardSetUnicodeString`)
- [ ] Linux hotkey provider (`XGrabKey` for X11; Wayland remains best-effort/non-blocking for v1)
- [ ] Linux keystroke typing (bundled native helper or `libxdo` P/Invoke; no required external utility for end users)
- [ ] Test on macOS Apple Silicon and Ubuntu
- [ ] **Exit criteria:** App works on Windows, macOS Apple Silicon, and Linux desktop on X11. Wayland is not a release blocker for v1.

### Milestone 6: Packaging & Distribution (Day 8-10)
- [ ] `dotnet publish` self-contained single-file for each platform
- [ ] Windows: optional MSIX installer
- [ ] macOS: `.app` bundle + DMG
- [ ] Linux: AppImage or `.deb`
- [ ] Bundle required native helpers so end users do not need separate packages for core dictation features
- [ ] GitHub Releases with automated builds
- [ ] **Exit criteria:** Users can download and run on any platform

### Milestone 7: Real-Time Streaming, Defaults, and Overlay Refresh (Planned)

**Goal**

Ship the first version of Hush that feels excellent out of the box:

- real microphone-to-text streaming through the Foundry Local live audio session API
- sensible defaults that require little or no setup on supported machines
- a genuinely useful overlay that shows live transcript text without stealing focus
- stronger platform reliability for hotkeys, audio capture, and text output

This milestone is the next implementation target after Milestone 6.

**Milestone number:** `7`

**In scope**

- Replace the current batch WAV workaround in `TranscriptionEngine` with the Foundry Local `LiveAudioTranscriptionSession` lifecycle (`StartAsync` → `AppendAsync` → `GetTranscriptionStream` → `StopAsync`)
- Keep the MVP safety rule: only committed text is typed into the focused app by default
- Wire persisted settings that materially affect the core dictation loop:
    - language
    - activation mode where supported
    - overlay visibility / placement settings that are actually implemented
    - microphone selection if added during this milestone
- Make the overlay display real transcript content, not just recording state
- Improve first-run defaults so Hush works well without requiring users to understand model/runtime internals
- Improve session reliability around start, stop, flush, cancellation, and backpressure
- Finish the next most important platform work needed for the core dictation path to feel production-ready

**Explicitly out of scope for Milestone 7**

- model picker, model catalog UI, model cleanup UI, or broader model management workflows
- custom dictionary, correction learning, or vocabulary boosting UI
- local transcript history, retention, retry browser, or note-taking features
- cloud transcription, AI agents, meeting transcription, or any productivity-suite expansion

**Design principle for this milestone**

Prefer good defaults over knobs. If a feature does not clearly improve the first five minutes of use, it should not expand the settings surface in Milestone 7.

#### Phase 7.1  Streaming Foundation

- [ ] Replace the polling/batch transcription loop with Foundry Local live streaming
- [ ] Use 16 kHz / 16-bit / mono PCM throughout the live path unless the SDK or backend requires otherwise
- [ ] Feed audio to the SDK in small real-time chunks (target: `100 ms` buffers unless testing shows a better default)
- [ ] Read live results from the SDK on a background task and normalize them into `TranscriptionResult`
- [ ] Preserve the app boundary that separates overlay display text from committed typed output
- [ ] Ensure final buffered audio is flushed on stop without waiting for a timer tick
- [ ] Ensure cancellation and disposal do not leak native sessions or background loops
- [ ] Remove no-longer-needed workaround code and comments tied to the file-based batch path

**Acceptance criteria**

- Holding the hotkey starts a real live streaming session rather than an 800 ms batch loop
- Releasing the hotkey flushes the final transcript without an extra delay window
- The engine no longer depends on temporary WAV files for the normal dictation path
- Existing dictation tests still pass, and new engine tests cover the live session lifecycle

#### Phase 7.2  Great Defaults and Core Reliability

- [ ] Use the saved language setting instead of hard-coding English in the engine
- [ ] Validate hotkey registration when settings are applied and surface actionable errors immediately
- [ ] Keep push-to-talk as the default where reliable key-up detection exists
- [ ] Fall back to tap-to-talk only on platforms or environments where release detection cannot be made robust
- [ ] Auto-select a sensible default microphone on startup
- [ ] If multiple devices exist, prefer the current system default device unless the user explicitly overrides it
- [ ] Improve startup and error messaging for missing microphone, denied permissions, or unavailable devices
- [ ] Finish the minimum platform work required for core dictation quality:
    - macOS microphone capture backend
    - Linux microphone capture backend for the supported desktop target
    - any paste/hotkey guardrails needed so the default path works reliably on supported platforms
- [ ] Remove or defer settings that are persisted but not actually honored by runtime behavior

**Acceptance criteria**

- A new user on a supported machine can launch Hush and dictate successfully without touching advanced settings
- The chosen language setting affects actual transcription sessions
- The app fails clearly when microphone capture or permissions are unavailable
- Supported platforms no longer ship with a "feature exists in settings but does nothing" experience for the core dictation flow

#### Phase 7.3  Overlay Refresh

- [ ] Redesign the overlay so it shows live transcript text and recording state together
- [ ] Keep it non-activating, click-through where appropriate, and never focus-stealing
- [ ] Preserve the audio meter, but make transcript readability the primary job of the overlay
- [ ] Show clear visual distinction between these states:
    - preparing / model warming
    - actively listening
    - transient error
    - idle / hidden
- [ ] Implement a more polished visual treatment while keeping the surface compact and desktop-native
- [ ] Make overlay position and opacity settings real, or remove them from Milestone 7 scope if they cannot be honored cleanly
- [ ] Do not add transcript history or editor-like interactions to the overlay

**Acceptance criteria**

- The overlay visibly renders live transcription text during an active dictation session
- The overlay remains readable on desktop backgrounds and multiple monitor setups
- It never steals focus from the target application
- Errors and readiness states are understandable without opening logs

#### Milestone 7 Default Choices

These defaults should be treated as the product defaults unless implementation evidence shows they are harmful:

- Audio format: `16 kHz`, `16-bit`, `mono`
- Real-time chunk size: `100 ms`
- Output policy: type committed text only
- Overlay behavior: visible during dictation, hidden when idle, bottom-center by default
- Microphone selection: current system default device
- Advanced features excluded from the first-run path: model selection, transcript storage, dictionary tuning

#### Milestone 7 Exit Criteria

- [ ] Hush uses the Foundry Local live audio transcription API for the main dictation path
- [ ] The overlay shows real live transcript text and looks materially more polished than the Milestone 6 version
- [ ] Core runtime settings used in the UI are wired into actual behavior, or removed from the UI
- [ ] Windows, macOS, and Linux each have a credible default dictation path for supported environments
- [ ] The app feels fast and understandable on first launch without extra configuration

#### Deferred Until Milestone 8+

The following items remain valid future work, but they are intentionally deferred until after Milestone 7 lands:

- model management UX
- custom dictionary / correction learning
- local transcript history
- richer note or meeting workflows

---

## 8  NuGet Dependencies

```xml
<!-- Hush.Core.csproj -->
<ItemGroup>
  <!-- Foundry Local managed SDK (unified, replaces WinML + old cross-platform variants) -->
  <PackageReference Include="Microsoft.AI.Foundry.Local" Version="1.0.0-dev.202604061825" />
  <!-- Explicit Core pin: avoids transitive dep version mismatch with local .nupkg -->
  <PackageReference Include="Microsoft.AI.Foundry.Local.Core" Version="1.0.0-dev-202604061808-7ad2ef0c" />

  <!-- Audio capture backends -->
  <PackageReference Include="NAudio" Version="2.*" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
  <!-- macOS/Linux capture backend package(s) selected after validation spike -->

  <!-- Logging -->
  <PackageReference Include="Microsoft.Extensions.Logging" Version="9.*" />
  <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="9.*" />
</ItemGroup>

<!-- Hush.App.csproj -->
<ItemGroup>
  <ProjectReference Include="..\Hush.Core\Hush.Core.csproj" />
  <PackageReference Include="Avalonia" Version="11.*" />
  <PackageReference Include="Avalonia.Desktop" Version="11.*" />
  <PackageReference Include="Avalonia.Themes.Fluent" Version="11.*" />
  <PackageReference Include="Avalonia.ReactiveUI" Version="11.*" />
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
</ItemGroup>
```

---

## 9  Settings Schema

```json
{
  "$schema": "https://hush.dev/settings.schema.json",
  "hotkey": "Ctrl+Shift+H",
  "language": "en",
  "transcriptionModel": "nemotron",
    "partialsInOverlay": true,
    "typeCommittedTextOnly": true,
  "overlayPosition": "bottom-center",
  "overlayOpacity": 0.85,
  "soundEffects": true,
    "clipboardFallback": false,
  "autoStart": false,
  "theme": "system"
}
```

---

## 10  Platform-Specific Considerations

### Windows
- Use `Microsoft.AI.Foundry.Local.WinML` for GPU/NPU acceleration
- `RegisterHotKey` / `UnregisterHotKey` via user32.dll for global hotkey
- `SendInput` with `KEYEVENTF_UNICODE` for keystroke typing (arbitrary Unicode, no VK mapping)
- Tray icon via Avalonia's `NativeMenu` / `TrayIcon`
- Optional: MSIX packaging for Store distribution

### macOS (Apple Silicon)
- Use `Microsoft.AI.Foundry.Local` (cross-platform variant)
- `CGEventTapCreate` for global hotkey capture (requires Accessibility permission)
- `CGEventCreateKeyboardEvent` + `CGEventKeyboardSetUnicodeString` for keystroke typing
- Accessibility permission prompt on first launch (required for both hotkey and typing)
- `.app` bundle with `Info.plist` for proper macOS integration

### Linux
- Use `Microsoft.AI.Foundry.Local` (cross-platform variant)
- X11 is the supported Linux target for v1; `XGrabKey` / `XUngrabKey` are acceptable there
- Prefer bundled native typing support or `libxdo` P/Invoke rather than requiring `xdotool` as an end-user dependency
- Wayland support is desirable but not a v1 release blocker until demand and test coverage justify it
- Audio capture must be backed by a Linux-compatible backend that does not require users to hand-install niche development tools
- AppImage for universal distribution

---

## 11  Data Flow Diagram

```
           HOTKEY PRESSED                     HOTKEY RELEASED
                │                                  │
                ▼                                  ▼
        ┌───────────────┐                  ┌───────────────┐
        │  Start Mic    │                  │  Stop Mic     │
        │  Capture      │                  │  Capture      │
        └──────┬────────┘                  └──────┬────────┘
               │                                  │
               ▼                                  ▼
        ┌───────────────┐                  ┌───────────────┐
        │  PCM chunks   │─ ─ ─ (stream) ─▶│  Stop Session │
        │  → AppendAsync│                  │  Flush final  │
        └──────┬────────┘                  └──────┬────────┘
               │                                  │
               ▼                                  ▼
        ┌───────────────┐                  ┌───────────────┐
        │  Nemotron     │                  │  Final        │
        │  Transcribes  │                  │  commit pass  │
        └──────┬────────┘                  └──────┬────────┘
               │                                  │
               ▼                                  ▼
        ┌───────────────┐                  ┌───────────────┐
        │  Committed    │                  │  Interim text │
        │  delta typed  │                  │  stays in UI  │
        │  via SendInput│                  └───────────────┘
        └──────┬────────┘
               │
               ▼
        ┌───────────────┐
        │  Overlay UI   │
        │  shows text   │
        └───────────────┘
```

---

## 12  Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Nemotron model large download (~1-2 GB) | Bad first-run experience | Show progress bar; allow background download; cache model |
| Global hotkey conflicts | Hotkey already in use | Allow user to customize; show clear error if registration fails |
| Accessibility permissions (macOS) | Hotkey won't work without it | Prompt with explanation on first launch; guide user to settings |
| Cross-platform audio backend gaps | Audio capture fails on macOS/Linux | Validate backend early, keep capture behind an abstraction, and choose separate backends per OS if needed |
| Foundry Local SDK API changes | Pre-release SDK breaks | Pin NuGet version; wrap SDK calls in abstraction layer |
| Keystroke injection blocked by app | Some apps (e.g. games, secure fields) ignore `SendInput` | Detect and warn user; offer optional clipboard-paste fallback |
| Typing speed vs. transcription speed | Chunks arrive faster than typing can finish | Queue chunks; use batch `SendInput` (all chars in one call) for near-instant delivery |
| Multiple mic devices | Wrong mic selected | Let user choose device in settings; default to system default |
| Unstable partial transcripts | Rewrites or backspaces could damage active user text | Show unstable partials in overlay only; type committed deltas into the app |
| Wayland incompatibilities | Linux feature gaps at launch | Support X11 for v1, keep Wayland best-effort until demand and test coverage increase |
| External helper dependencies | Packaging becomes fragile | Bundle native helpers when possible and avoid required external CLI tools for end users |

---

## 13  Future Enhancements (Post-MVP)

These are NOT in the MVP scope but inform architectural decisions:

- **Toggle mode** — press once to start, again to stop (in addition to push-to-talk)
- **Custom vocabulary** — user-defined words/abbreviations for better recognition
- **Transcription history** — searchable log of past dictations
- **File transcription** — drag & drop audio/video files for offline transcription
- **Meeting recording** — long-form transcription with speaker diarization
- **Multiple languages** — language auto-detection or per-session language setting
- **LLM post-processing** — optional local LLM cleanup/rewriting via Foundry Local chat models

---

## 14  Current State & Known Workarounds

- Hush pins pre-release `Microsoft.AI.Foundry.Local` NuGet packages (see `NuGet.config` for the local package source).
- The managed Foundry Local SDK handles native-asset resolution and DLL path wiring. Hush supplies an `AppName` via `FoundryRuntimeConfiguration`.
- macOS and Linux do not yet have validated microphone capture backends. Hotkey and text-output paths work, but audio capture on those platforms is pending.
- The Nemotron CPU int4 model is used in place of Whisper Tiny for better streaming quality. See `dist/setup.ps1` for the model swap workflow.
- **Whisper fallback** — option to use `whisper-tiny` for lower resource usage on constrained hardware.
- **Clipboard-paste fallback** — optional mode for apps that block simulated keystrokes.
- **Notification integration** — toast notifications instead of overlay (future).
- **Plugin system** — extensible output targets (future).

---

## 15  Open Questions

1. **Foundry Local stream semantics** — Need to confirm the exact C# event contract for live transcription: interim-only, final-only, or both, and whether the model alias is exactly `"nemotron"` in the catalog.
2. **Cross-platform capture backend** — Need to confirm the most reliable macOS/Linux audio backend and whether NAudio is sufficient anywhere beyond Windows.
3. **Bundled native helpers** — Need to decide which Linux/macOS native libraries or helper binaries can be legally and practically bundled for self-contained distribution.
4. **Wayland promotion criteria** — Define the demand threshold and test matrix that would move Wayland from best-effort to fully supported.

---

## 16  Getting Started (Quick Start for Contributors)

```bash
# Clone
git clone https://github.com/maanavdalal/hush.git
cd hush

# Build
dotnet build

# Run (downloads model on first launch)
dotnet run --project src/Hush.App

# Test
dotnet test
```

---

*Last updated: April 7, 2026*

---

## 17  Beta Testing Notes

> **Note for contributors:** This section describes a temporary model-swap workaround. It may become unnecessary as the Foundry Local SDK evolves.

### 17.1  Nemotron CPU Model (No GPU Required)

The default Foundry Local catalog only includes **Whisper** (all variants require CUDA GPU).
For testers without a GPU, a CPU-quantized int4 Nemotron model is available as a workaround.

**Model source:** https://huggingface.co/jiafatom/nemotron-cpu-int4/tree/main

**Steps:**

1. Find the Foundry Local model cache directory — it is `%USERPROFILE%\.aitk\Microsoft\` on Windows.
   The whisper-tiny CPU slot is: `openai-whisper-tiny-generic-cpu-2\cpu-fp32\`.

2. Download all files from the HuggingFace repo above into that folder, replacing the existing Whisper files:
   - `encoder.onnx` + `encoder.onnx.data`
   - `decoder.onnx` + `decoder.onnx.data`
   - `joint.onnx` + `joint.onnx.data`
   - `genai_config.json`, `audio_processor_config.json`, `tokenizer.json`, `tokenizer_config.json`, `vocab.txt`

3. In `~/.hush/settings.json`, set:
   ```json
   { "transcriptionModel": "whisper-tiny" }
   ```
   Hush will load the Nemotron weights under the `whisper-tiny` alias via the SDK.

> **Warning:** This is an unsupported workaround. The model format must be compatible with the Foundry Local ONNX runtime. Accuracy and latency will differ from Whisper. Not recommended for standard setups. GPU users should use `whisper-base` or larger.

