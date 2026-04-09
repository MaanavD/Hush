// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Hush.Core.Audio;
using Hush.Core.Configuration;

namespace Hush.App.ViewModels;

/// <summary>
/// View-model for the Settings window.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly HushSettings _settings;

    [ObservableProperty] private string _hotkey;
    [ObservableProperty] private string _language;
    [ObservableProperty] private string _transcriptionModel;
    [ObservableProperty] private bool _partialsInOverlay;
    [ObservableProperty] private string _overlayPosition;
    [ObservableProperty] private double _overlayOpacity;
    [ObservableProperty] private bool _soundEffects;
    [ObservableProperty] private bool _streamingCommit;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private int _selectedMicrophoneIndex;

    public ObservableCollection<MicrophoneDevice> AvailableMicrophones { get; } = new();

    public SettingsViewModel(HushSettings settings)
    {
        _settings = settings;
        _hotkey = settings.Hotkey;
        _language = settings.Language;
        _transcriptionModel = settings.TranscriptionModel;
        _partialsInOverlay = settings.PartialsInOverlay;
        _overlayPosition = settings.OverlayPosition;
        _overlayOpacity = settings.OverlayOpacity;
        _soundEffects = settings.SoundEffects;
        _streamingCommit = settings.StreamingCommit;
        _autoStart = settings.AutoStart;

        RefreshMicrophones();
        _selectedMicrophoneIndex = FindMicIndex(settings.MicrophoneDeviceIndex);
    }

    public void RefreshMicrophones()
    {
        AvailableMicrophones.Clear();
#if WINDOWS
        foreach (var (index, name) in AudioCaptureService.GetAvailableDevices())
            AvailableMicrophones.Add(new MicrophoneDevice(index, name));
#else
        AvailableMicrophones.Add(new MicrophoneDevice(-1, "System Default"));
#endif
    }

    private int FindMicIndex(int deviceIndex)
    {
        for (int i = 0; i < AvailableMicrophones.Count; i++)
        {
            if (AvailableMicrophones[i].DeviceIndex == deviceIndex)
                return i;
        }
        return 0; // Fall back to system default.
    }

    /// <summary>Copies VM state back into the backing <see cref="HushSettings"/> object.</summary>
    public void Apply()
    {
        _settings.Hotkey = Hotkey;
        _settings.Language = Language;
        _settings.TranscriptionModel = TranscriptionModel;
        _settings.PartialsInOverlay = PartialsInOverlay;
        _settings.OverlayPosition = OverlayPosition;
        _settings.OverlayOpacity = OverlayOpacity;
        _settings.SoundEffects = SoundEffects;
        _settings.StreamingCommit = StreamingCommit;
        _settings.AutoStart = AutoStart;
        _settings.MicrophoneDeviceIndex = SelectedMicrophoneIndex >= 0 && SelectedMicrophoneIndex < AvailableMicrophones.Count
            ? AvailableMicrophones[SelectedMicrophoneIndex].DeviceIndex
            : -1;
    }
}

public sealed record MicrophoneDevice(int DeviceIndex, string Name)
{
    public override string ToString() => Name;
}
