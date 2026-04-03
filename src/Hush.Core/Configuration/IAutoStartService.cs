namespace Hush.Core.Configuration;

/// <summary>
/// Controls whether Hush is registered to launch automatically at OS login.
/// </summary>
public interface IAutoStartService
{
    /// <summary>
    /// Enables or disables OS-level login auto-start for Hush.
    /// </summary>
    Task SetEnabledAsync(bool enable, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> if Hush is currently registered as a login item.
    /// </summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);
}
