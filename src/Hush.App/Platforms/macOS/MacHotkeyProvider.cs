using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hush.Core.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.App.Platforms.macOS;

/// <summary>
/// Global hotkey provider for macOS using a session-level <c>CGEventTap</c>.
/// Runs a dedicated <c>CFRunLoop</c> on a background thread so the tap can
/// receive system-wide keyboard events without interfering with Avalonia's
/// main loop.
/// </summary>
/// <remarks>
/// Requires the <b>Accessibility</b> permission to be granted to the app the
/// first time it runs. If the permission is absent, <c>CGEventTapCreate</c>
/// returns <see cref="IntPtr.Zero"/> and a <see cref="PlatformNotSupportedException"/>
/// is thrown from <see cref="Register"/>.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacHotkeyProvider : IGlobalHotkeyService
{
    // ── CoreGraphics ─────────────────────────────────────────────────────────
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CG)] private static extern nint CGEventTapCreate(
        int tapLocation, int tapPlacement, int tapOptions, ulong eventMask,
        // Pointer to unmanaged callback  (unsafe function pointer stored in field)
        nint callback, nint userInfo);

    [DllImport(CG)] private static extern ulong CGEventGetIntegerValueField(nint @event, int field);
    [DllImport(CG)] private static extern ulong CGEventGetFlags(nint @event);
    [DllImport(CG)] private static extern void CGEventTapEnable(nint tap, bool enable);

    // ── CoreFoundation ───────────────────────────────────────────────────────
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CF)] private static extern nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);
    [DllImport(CF)] private static extern nint CFRunLoopGetCurrent();
    [DllImport(CF)] private static extern void CFRunLoopAddSource(nint rl, nint source, nint mode);
    [DllImport(CF)] private static extern void CFRunLoopRun();
    [DllImport(CF)] private static extern void CFRunLoopStop(nint rl);
    [DllImport(CF)] private static extern void CFRelease(nint cf);

    // ── CGEvent constants ────────────────────────────────────────────────────
    private const int kCGSessionEventTap = 1;
    private const int kCGHeadInsertEventTap = 0;
    private const int kCGEventTapOptionListenOnly = 1;
    private const uint kCGEventKeyDown = 10;
    private const uint kCGEventKeyUp = 11;
    private const uint kCGEventFlagsChanged = 12;
    // Mask = 1 << type
    private const ulong kCGEventMaskKeyDown = 1UL << 10;
    private const ulong kCGEventMaskKeyUp = 1UL << 11;
    private const ulong kCGEventMaskFlagsChanged = 1UL << 12;
    private const int kCGKeyboardEventVirtualKey = 9;

    // Modifier flag masks in CGEventFlags
    private const ulong kMaskShift = 0x00020000UL;
    private const ulong kMaskControl = 0x00040000UL;
    private const ulong kMaskOption = 0x00080000UL;
    private const ulong kMaskCommand = 0x00100000UL;

    // macOS Carbon virtual-key codes (HIToolbox/Events.h)
    private static readonly Dictionary<string, ushort> s_keyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 49, ["return"] = 36, ["tab"] = 48, ["escape"] = 53, ["delete"] = 51,
        ["left"] = 123, ["right"] = 124, ["down"] = 125, ["up"] = 126,
        ["f1"]=122,["f2"]=120,["f3"]=99,["f4"]=118,["f5"]=96,["f6"]=97,
        ["f7"]=98,["f8"]=100,["f9"]=101,["f10"]=109,["f11"]=103,["f12"]=111,
        ["a"]=0,["b"]=11,["c"]=8,["d"]=2,["e"]=14,["f"]=3,["g"]=5,
        ["h"]=4,["i"]=34,["j"]=38,["k"]=40,["l"]=37,["m"]=46,["n"]=45,
        ["o"]=31,["p"]=35,["q"]=12,["r"]=15,["s"]=1,["t"]=17,["u"]=32,
        ["v"]=9,["w"]=13,["x"]=7,["y"]=16,["z"]=6,
    };

    // ── State ────────────────────────────────────────────────────────────────
    [ThreadStatic] private static MacHotkeyProvider? t_current;

    private readonly ILogger<MacHotkeyProvider> _logger;
    private GCHandle _selfHandle;
    private nint _machPort;
    private nint _runLoop;
    private Thread? _thread;
    private ushort _targetVk;
    private ulong _targetModMask;
    private volatile bool _isHeld;
    private volatile bool _disposed;

    // Keep delegate alive to prevent GC collection.
    private readonly unsafe delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint> _callbackPtr;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

    public MacHotkeyProvider(ILogger<MacHotkeyProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<MacHotkeyProvider>.Instance;
        unsafe { _callbackPtr = &TapCallback; }
    }

    /// <inheritdoc/>
    public void Register(string hotkey)
    {
        ParseHotkey(hotkey, out _targetVk, out _targetModMask);

        _selfHandle = GCHandle.Alloc(this);
        ulong eventMask = kCGEventMaskKeyDown | kCGEventMaskKeyUp | kCGEventMaskFlagsChanged;

        unsafe
        {
            _machPort = CGEventTapCreate(
                kCGSessionEventTap,
                kCGHeadInsertEventTap,
                kCGEventTapOptionListenOnly,
                eventMask,
                (nint)_callbackPtr,
                GCHandle.ToIntPtr(_selfHandle));
        }

        if (_machPort == 0)
        {
            _selfHandle.Free();
            throw new PlatformNotSupportedException(
                "CGEventTapCreate returned null. Grant Accessibility permission to Hush " +
                "in System Settings → Privacy & Security → Accessibility, then restart the app.");
        }

        _thread = new Thread(RunLoop) { IsBackground = true, Name = "Hush.MacHotkeyRunLoop" };
        _thread.Start();
        _logger.LogInformation("macOS hotkey '{Hotkey}' registered (vk={Vk}, mods=0x{Mods:X}).",
            hotkey, _targetVk, _targetModMask);
    }

    private void RunLoop()
    {
        _runLoop = CFRunLoopGetCurrent();

        var source = CFMachPortCreateRunLoopSource(0, _machPort, 0);
        // kCFRunLoopDefaultMode — look up via NativeLibrary to avoid hard-coding the string value.
        var mode = GetDefaultRunLoopMode();
        CFRunLoopAddSource(_runLoop, source, mode);
        CFRelease(source);

        CFRunLoopRun(); // blocks until CFRunLoopStop is called
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static nint TapCallback(nint proxy, uint type, nint @event, nint userInfo)
    {
        if (@event == 0) return 0;

        if (GCHandle.FromIntPtr(userInfo).Target is MacHotkeyProvider self)
            self.OnEvent(type, @event);

        return @event;
    }

    private void OnEvent(uint type, nint @event)
    {
        if (type == kCGEventKeyDown)
        {
            var vk = (ushort)CGEventGetIntegerValueField(@event, kCGKeyboardEventVirtualKey);
            var flags = CGEventGetFlags(@event) & (kMaskShift | kMaskControl | kMaskOption | kMaskCommand);
            if (vk == _targetVk && flags == _targetModMask && !_isHeld)
            {
                _isHeld = true;
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (type == kCGEventKeyUp)
        {
            var vk = (ushort)CGEventGetIntegerValueField(@event, kCGKeyboardEventVirtualKey);
            if (vk == _targetVk && _isHeld)
            {
                _isHeld = false;
                HotkeyReleased?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (type == kCGEventFlagsChanged && _isHeld)
        {
            // If a required modifier is released before the main key, treat as chord release.
            var flags = CGEventGetFlags(@event) & (kMaskShift | kMaskControl | kMaskOption | kMaskCommand);
            if ((flags & _targetModMask) != _targetModMask)
            {
                _isHeld = false;
                HotkeyReleased?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <inheritdoc/>
    public void Unregister()
    {
        if (_runLoop != 0)
            CFRunLoopStop(_runLoop);

        if (_machPort != 0)
        {
            CGEventTapEnable(_machPort, false);
            CFRelease(_machPort);
            _machPort = 0;
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void ParseHotkey(string hotkey, out ushort vkCode, out ulong modMask)
    {
        vkCode = 0;
        modMask = 0;

        foreach (var part in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modMask |= kMaskControl; break;
                case "shift":             modMask |= kMaskShift; break;
                case "alt" or "option":   modMask |= kMaskOption; break;
                case "cmd" or "command":  modMask |= kMaskCommand; break;
                default:
                    if (s_keyCodes.TryGetValue(part, out var code))
                        vkCode = code;
                    else if (part.Length == 1)
                        s_keyCodes.TryGetValue(part.ToLower(), out vkCode);
                    break;
            }
        }
    }

    private static nint GetDefaultRunLoopMode()
    {
        var lib = NativeLibrary.Load(CF);
        var modeAddr = NativeLibrary.GetExport(lib, "kCFRunLoopDefaultMode");
        return Marshal.ReadIntPtr(modeAddr); // export is a pointer-to-CFStringRef
    }
}

