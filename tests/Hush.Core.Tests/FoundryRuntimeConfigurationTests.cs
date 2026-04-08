// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Transcription;

namespace Hush.Core.Tests;

public sealed class FoundryRuntimeConfigurationTests
{
    [Fact]
    public void Create_ReturnsConfiguration_WithCorrectAppName()
    {
        var config = FoundryRuntimeConfiguration.Create("Hush");

        Assert.IsType<Microsoft.AI.Foundry.Local.Configuration>(config);
        Assert.Equal("Hush", config.AppName);
    }

    [Fact]
    public void Create_WithLogger_DoesNotThrow()
    {
        var ex = Record.Exception(() => FoundryRuntimeConfiguration.Create("Hush", logger: null));

        Assert.Null(ex);
    }

    [Fact]
    public void Create_WithDifferentAppName_SetsCorrectName()
    {
        var config = FoundryRuntimeConfiguration.Create("TestApp");

        Assert.Equal("TestApp", config.AppName);
    }
}
