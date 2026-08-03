// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.App.ViewModels;

namespace Hush.App.Tests;

public sealed class PostProcessingWarmupControllerTests
{
    [Fact]
    public void Begin_CancelsPreviousOperation()
    {
        using var controller = new PostProcessingWarmupController();
        using var first = controller.Begin();

        using var second = controller.Begin();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        Assert.True(second.IsCurrent);
    }

    [Fact]
    public void CompletingOldOperation_DoesNotClearCurrentOperation()
    {
        using var controller = new PostProcessingWarmupController();
        var first = controller.Begin();
        using var second = controller.Begin();

        first.Dispose();

        Assert.False(first.IsCurrent);
        Assert.True(second.IsCurrent);
        Assert.True(second.IsLatestVersion);
    }

    [Fact]
    public void Begin_LinksExternalCancellationToken()
    {
        using var controller = new PostProcessingWarmupController();
        using var external = new CancellationTokenSource();
        using var operation = controller.Begin(external.Token);

        external.Cancel();

        Assert.True(operation.Token.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_CancelsCurrentOperation()
    {
        var controller = new PostProcessingWarmupController();
        var operation = controller.Begin();
        var token = operation.Token;

        controller.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        operation.Dispose();
    }
}
