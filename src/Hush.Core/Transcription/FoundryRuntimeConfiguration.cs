// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Hush.Core.Transcription;

/// <summary>
/// Builds the Foundry Local SDK <see cref="Microsoft.AI.Foundry.Local.Configuration"/>.
/// With the 1.0.0-dev managed SDK, native-asset resolution and DLL path wiring are handled
/// entirely by the SDK and the NuGet-deployed runtimes assets; Hush only needs to supply
/// an <c>AppName</c>.
/// </summary>
public static class FoundryRuntimeConfiguration
{
    /// <summary>Creates a <see cref="Microsoft.AI.Foundry.Local.Configuration"/> for Hush.</summary>
    public static Microsoft.AI.Foundry.Local.Configuration Create(string appName, ILogger? logger = null)
    {
        return new Microsoft.AI.Foundry.Local.Configuration
        {
            AppName = appName,
        };
    }
}
