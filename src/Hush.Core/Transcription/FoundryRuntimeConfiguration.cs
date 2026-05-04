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
    /// <summary>
    /// TEMPORARY: catalog filter that exposes pre-release/test models (e.g. qwen3.5-*, qwen3-vl-*)
    /// not yet promoted to the public Foundry Local catalog. Remove once these models ship publicly.
    /// See README "Temporary catalog filter" section.
    /// </summary>
    private const string TemporaryAzureCatalogFilter = "'',test";

    /// <summary>Creates a <see cref="Microsoft.AI.Foundry.Local.Configuration"/> for Hush.</summary>
    public static Microsoft.AI.Foundry.Local.Configuration Create(string appName, ILogger? logger = null)
    {
        return new Microsoft.AI.Foundry.Local.Configuration
        {
            AppName = appName,
            // TODO(temp): drop AdditionalSettings once Qwen3.5 / Qwen3-VL land in the default catalog.
            AdditionalSettings = new Dictionary<string, string>
            {
                { "AzureCatalogFilter", TemporaryAzureCatalogFilter },
            },
        };
    }
}
