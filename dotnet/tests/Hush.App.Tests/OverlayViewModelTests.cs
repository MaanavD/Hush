// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

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
        Assert.False(vm.IsModelReady);
        Assert.Equal(0.0, vm.ModelDownloadProgress);
        Assert.Equal(0f, vm.AudioLevel);
        Assert.Equal("bottom-center", vm.OverlayPosition);
        Assert.Equal(0.85, vm.OverlayOpacity);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.HasError);
        Assert.False(vm.HasTranscript);
        Assert.False(vm.ShowListeningHint);
        Assert.Equal(string.Empty, vm.TranscriptText);
        Assert.True(vm.IsPreparing);
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

    [Fact]
    public void BeginSession_WithPartialsEnabled_ClearsTranscript()
    {
        var vm = new OverlayViewModel();
        vm.TranscriptText = "stale text";

        vm.BeginSession(showPartialTranscript: true);
        vm.IsListening = true;

        Assert.Equal(string.Empty, vm.TranscriptText);
        Assert.False(vm.HasTranscript);
        Assert.True(vm.ShowListeningHint);
    }

    [Fact]
    public void UpdateInterimTranscript_WithPartialsEnabled_ShowsLiveText()
    {
        var vm = new OverlayViewModel();
        vm.BeginSession(showPartialTranscript: true);
        vm.IsListening = true;

        vm.UpdateInterimTranscript("hello world");

        Assert.Equal("hello world", vm.TranscriptText);
        Assert.True(vm.HasTranscript);
        Assert.False(vm.ShowListeningHint);
    }

    [Fact]
    public void AppendCommittedTranscript_WithPartialsDisabled_ShowsCommittedOnly()
    {
        var vm = new OverlayViewModel();
        vm.BeginSession(showPartialTranscript: false);
        vm.IsListening = true;

        vm.UpdateInterimTranscript("hello unstable");
        Assert.Equal(string.Empty, vm.TranscriptText);

        vm.AppendCommittedTranscript("hello");
        vm.AppendCommittedTranscript(" world");

        Assert.Equal("hello world", vm.TranscriptText);
        Assert.True(vm.HasTranscript);
    }

    [Fact]
    public void ClearSessionTranscript_RemovesVisibleText()
    {
        var vm = new OverlayViewModel();
        vm.BeginSession(showPartialTranscript: true);
        vm.UpdateInterimTranscript("hello world");

        vm.ClearSessionTranscript();

        Assert.Equal(string.Empty, vm.TranscriptText);
        Assert.False(vm.HasTranscript);
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
    public void TranscriptText_RaisesDependentProperties()
    {
        var vm = new OverlayViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.TranscriptText = "hello";

        Assert.Contains("TranscriptText", raised);
        Assert.Contains("HasTranscript", raised);
        Assert.Contains("ShowListeningHint", raised);
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
        Assert.False(vm.IsPreparing);

        // Phase 3: Listening
        vm.IsListening = true;
        vm.AudioLevel = 0.7f;
        Assert.True(vm.IsListening);

        // Phase 4: Stopped
        vm.IsListening = false;
        vm.AudioLevel = 0f;
        Assert.False(vm.IsListening);
        Assert.Equal(0f, vm.AudioLevel);
    }

    [Fact]
    public void OverlayAppearanceSettings_AreStored()
    {
        var vm = new OverlayViewModel
        {
            OverlayOpacity = 0.64,
            OverlayPosition = "top-right"
        };

        Assert.Equal(0.64, vm.OverlayOpacity);
        Assert.Equal("top-right", vm.OverlayPosition);
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

        Assert.False(vm.HasError);
        Assert.True(vm.IsListening);
    }
}
