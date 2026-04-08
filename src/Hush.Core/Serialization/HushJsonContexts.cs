// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Text.Json.Serialization;
using Hush.Core.Configuration;

namespace Hush.Core.Configuration
{
    /// <summary>
    /// Source-generated JSON serialization context for <see cref="HushSettings"/>.
    /// Using source generation avoids runtime reflection, which is required for
    /// Native AOT compatibility and improves startup performance.
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(HushSettings))]
    internal sealed partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}