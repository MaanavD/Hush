using Hush.App.ViewModels;

namespace Hush.App.Tests;

/// <summary>
/// Tests for <see cref="OverlayViewModel"/> covering state transitions
/// and property change notifications critical for the overlay UI.
/// </summary>
public sealed class OverlayViewModelTests
{
    // ── Default state ────────────────────────────────────────────────────

    [Fact]
    public void DefaultState_NotListening()
    {
        var vm = new OverlayViewModel();
        Assert.False(vm.IsListening);
        Assert.Equal(string.Empty, vm.InterimText);
        Assert.False(vm.IsModelReady);
        Assert.Equal(0.0, vm.ModelDownloadProgress);
        Assert.Equal(0f, vm.AudioLevel);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.HasError);
    }

    // ── Error state ──────────────────────────────────────────────────────

    [Fact]
    public void HasError_TrueWhenErrorMessageSet()
    {
        var vm = new OverlayViewModel();
        vm.ErrorMessage = "Microphone unavailable";
        Assert.True(vm.HasError);
    }

    [Fact]
    public void HasError_FalseWhenErrorMessageCleared()
    {
        var vm = new OverlayViewModel();
        vm.ErrorMessage = "Some error";
        Assert.True(vm.HasError);

        vm.ErrorMessage = null;
        Assert.False(vm.HasError);
    }

    [Fact]
    public void HasError_FalseForEmptyString()
    {
        var vm = new OverlayViewModel();
        vm.ErrorMessage = string.Empty;
        Assert.False(vm.HasError);
    }

    // ── Property change notifications ────────────────────────────────────

    [Fact]
    public void ErrorMessage_RaisesHasErrorPropertyChanged()
    {
        var vm = new OverlayViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.ErrorMessage = "test error";

        Assert.Contains("ErrorMessage", raised);
        Assert.Contains("HasError", raised);
    }

    [Fact]
    public void IsListening_RaisesPropertyChanged()
    {
        var vm = new OverlayViewModel();
        bool raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "IsListening") raised = true;
        };

        vm.IsListening = true;
        Assert.True(raised);
    }

    [Fact]
    public void InterimText_RaisesPropertyChanged()
    {
        var vm = new OverlayViewModel();
        bool raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "InterimText") raised = true;
        };

        vm.InterimText = "hello world";
        Assert.True(raised);
    }

    // ── Audio level ──────────────────────────────────────────────────────

    [Fact]
    public void AudioLevel_ClampsCorrectly()
    {
        var vm = new OverlayViewModel();
        vm.AudioLevel = 0.5f;
        Assert.Equal(0.5f, vm.AudioLevel);

        vm.AudioLevel = 0f;
        Assert.Equal(0f, vm.AudioLevel);

        vm.AudioLevel = 1f;
        Assert.Equal(1f, vm.AudioLevel);
    }

    // ── Model download progress ──────────────────────────────────────────

    [Fact]
    public void ModelDownloadProgress_ZeroToOne()
    {
        var vm = new OverlayViewModel();
        vm.ModelDownloadProgress = 0.0;
        Assert.Equal(0.0, vm.ModelDownloadProgress);

        vm.ModelDownloadProgress = 0.5;
        Assert.Equal(0.5, vm.ModelDownloadProgress);

        vm.ModelDownloadProgress = 1.0;
        Assert.Equal(1.0, vm.ModelDownloadProgress);
    }

    // ── State transition: download → ready → listening → stopped ─────────

    [Fact]
    public void FullLifecycleTransition()
    {
        var vm = new OverlayViewModel();

        // Phase 1: Downloading
        Assert.False(vm.IsModelReady);
        vm.ModelDownloadProgress = 0.5;
        Assert.Equal(0.5, vm.ModelDownloadProgress);

        // Phase 2: Model ready
        vm.IsModelReady = true;
        Assert.True(vm.IsModelReady);

        // Phase 3: Listening
        vm.IsListening = true;
        vm.InterimText = "the quick brown fox";
        vm.AudioLevel = 0.7f;
        Assert.True(vm.IsListening);
        Assert.Equal("the quick brown fox", vm.InterimText);

        // Phase 4: Stopped
        vm.IsListening = false;
        vm.AudioLevel = 0f;
        Assert.False(vm.IsListening);
        Assert.Equal(0f, vm.AudioLevel);
    }

    // ── Error during listening ───────────────────────────────────────────

    [Fact]
    public void ErrorDuringListening_ShowsError()
    {
        var vm = new OverlayViewModel();
        vm.IsModelReady = true;
        vm.IsListening = true;

        // Mic disconnects mid-session
        vm.IsListening = false;
        vm.ErrorMessage = "Microphone disconnected";

        Assert.False(vm.IsListening);
        Assert.True(vm.HasError);
        Assert.Equal("Microphone disconnected", vm.ErrorMessage);
    }

    // ── Error cleared on next session ────────────────────────────────────

    [Fact]
    public void ErrorCleared_OnNewSession()
    {
        var vm = new OverlayViewModel();
        vm.ErrorMessage = "Previous error";
        Assert.True(vm.HasError);

        // New session starts
        vm.ErrorMessage = null;
        vm.IsListening = true;
        vm.InterimText = string.Empty;

        Assert.False(vm.HasError);
        Assert.True(vm.IsListening);
    }
}
