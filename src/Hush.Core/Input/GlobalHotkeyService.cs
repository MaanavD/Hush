using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Input;

/// <summary>
/// Aggregates the per-platform hotkey provider behind the
/// <see cref="IGlobalHotkeyService"/> interface.
/// On Windows the concrete provider is
/// <c>Hush.App.Platforms.Windows.WindowsHotkeyProvider</c>. The platform
/// DI registration wires the correct provider at startup.
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private readonly IGlobalHotkeyService _inner;
    private readonly ILogger<GlobalHotkeyService> _logger;

    public GlobalHotkeyService(
        IGlobalHotkeyService platformProvider,
        ILogger<GlobalHotkeyService>? logger = null)
    {
        _inner = platformProvider;
        _logger = logger ?? NullLogger<GlobalHotkeyService>.Instance;

        _inner.HotkeyPressed += (s, e) => HotkeyPressed?.Invoke(s, e);
        _inner.HotkeyReleased += (s, e) => HotkeyReleased?.Invoke(s, e);
    }

    /// <inheritdoc/>
    public event EventHandler? HotkeyPressed;

    /// <inheritdoc/>
    public event EventHandler? HotkeyReleased;

    /// <inheritdoc/>
    public void Register(string hotkey)
    {
        _logger.LogInformation("Registering global hotkey: {Hotkey}", hotkey);
        _inner.Register(hotkey);
    }

    /// <inheritdoc/>
    public void Unregister()
    {
        _logger.LogInformation("Unregistering global hotkey.");
        _inner.Unregister();
    }

    /// <inheritdoc/>
    public void Dispose() => _inner.Dispose();
}
