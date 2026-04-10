// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hush.Core.Audio;
using Hush.Core.Configuration;
using Hush.Core.PostProcessing;

namespace Hush.App.ViewModels
{
    /// <summary>
    /// View-model for the Settings window.
    /// </summary>
    public sealed partial class SettingsViewModel : ObservableObject
    {
        private readonly HushSettings _settings;

        // Input
        [ObservableProperty] private string _hotkey;
        [ObservableProperty] private string _language;
        [ObservableProperty] private int _selectedMicrophoneIndex;

        // Model
        [ObservableProperty] private string _transcriptionModel;

        // Behavior
        [ObservableProperty] private bool _partialsInOverlay;
        [ObservableProperty] private string _overlayPosition;
        [ObservableProperty] private double _overlayOpacity;
        [ObservableProperty] private bool _soundEffects;
        [ObservableProperty] private bool _streamingCommit;
        [ObservableProperty] private bool _autoStart;

        // Clean Mode
        [ObservableProperty] private string _cleanHotkey = "Alt+H";
        [ObservableProperty] private bool _postProcessingEnabled = true;
        [ObservableProperty] private string _postProcessingModel = "qwen3-0.6b";
        [ObservableProperty] private string? _activePostProcessingPromptId;

        // Prompts
        public ObservableCollection<LlmPromptViewModel> AllPrompts { get; } = new();
        [ObservableProperty] private LlmPromptViewModel? _selectedPrompt;
        [ObservableProperty] private bool _isEditingPrompt;
        [ObservableProperty] private string _editingPromptName = string.Empty;
        [ObservableProperty] private string _editingPromptText = string.Empty;
        [ObservableProperty] private string? _editingPromptId;

        public IRelayCommand AddPromptCommand { get; }
        public IRelayCommand<LlmPromptViewModel?> EditPromptCommand { get; }
        public IRelayCommand<LlmPromptViewModel?> DeletePromptCommand { get; }
        public IRelayCommand SavePromptCommand { get; }
        public IRelayCommand CancelEditCommand { get; }

        // Dictionary
        public ObservableCollection<SubstitutionRuleViewModel> CustomSubstitutions { get; } = new();
        public IRelayCommand AddSubstitutionCommand { get; }
        public IRelayCommand<SubstitutionRuleViewModel?> RemoveSubstitutionCommand { get; }

        public bool HasCustomSubstitutions => CustomSubstitutions.Count > 0;

        // Behavior additions
        [ObservableProperty] private string _autoSubmitKey = "None";
        [ObservableProperty] private string _modelUnloadTimeout = "After 5 min";

        public static IReadOnlyList<string> AutoSubmitKeyOptions { get; } =
            new[] { "None", "Enter", "Ctrl+Enter" };

        public static IReadOnlyList<string> ModelUnloadTimeoutOptions { get; } =
            new[] { "Never", "After 2 min", "After 5 min", "After 15 min" };

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

            // Clean Mode
            _cleanHotkey = settings.CleanHotkey;
            _postProcessingEnabled = settings.PostProcessingEnabled;
            _postProcessingModel = settings.PostProcessingModel;

            // Behavior additions — convert enums to display strings
            _autoSubmitKey = settings.AutoSubmitKey switch
            {
                Hush.Core.Configuration.AutoSubmitKey.Enter      => "Enter",
                Hush.Core.Configuration.AutoSubmitKey.CtrlEnter  => "Ctrl+Enter",
                _                                                => "None",
            };
            _modelUnloadTimeout = settings.ModelUnloadTimeout switch
            {
                Hush.Core.Configuration.ModelUnloadTimeout.Min2  => "After 2 min",
                Hush.Core.Configuration.ModelUnloadTimeout.Min5  => "After 5 min",
                Hush.Core.Configuration.ModelUnloadTimeout.Min15 => "After 15 min",
                _                                                => "Never",
            };

            // Prompts — built-ins first, then user-defined
            foreach (var p in HushSettings.BuiltInPrompts)
                AllPrompts.Add(new LlmPromptViewModel(p.Id, p.Name, p.Prompt, isBuiltIn: true));
            foreach (var p in settings.PostProcessingPrompts)
                AllPrompts.Add(new LlmPromptViewModel(p.Id, p.Name, p.Prompt, isBuiltIn: false));

            var activeId = settings.ActivePostProcessingPromptId;
            _selectedPrompt = AllPrompts.FirstOrDefault(p => p.Id == activeId)
                              ?? AllPrompts.FirstOrDefault();

            // Custom substitutions
            foreach (var s in settings.CustomSubstitutions)
                CustomSubstitutions.Add(new SubstitutionRuleViewModel { Match = s.Match, Replace = s.Replace });

            CustomSubstitutions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCustomSubstitutions));

            // Commands
            AddPromptCommand = new RelayCommand(() =>
            {
                EditingPromptId = null;
                EditingPromptName = string.Empty;
                EditingPromptText = string.Empty;
                IsEditingPrompt = true;
            });

            EditPromptCommand = new RelayCommand<LlmPromptViewModel?>(p =>
            {
                if (p is null || !p.IsUserDefined) return;
                EditingPromptId = p.Id;
                EditingPromptName = p.Name;
                EditingPromptText = p.Prompt;
                IsEditingPrompt = true;
            });

            DeletePromptCommand = new RelayCommand<LlmPromptViewModel?>(p =>
            {
                if (p is null || !p.IsUserDefined) return;
                AllPrompts.Remove(p);
            });

            SavePromptCommand = new RelayCommand(() =>
            {
                if (EditingPromptId is null)
                {
                    var newPrompt = new LlmPromptViewModel(
                        Guid.NewGuid().ToString(),
                        EditingPromptName,
                        EditingPromptText,
                        isBuiltIn: false);
                    AllPrompts.Add(newPrompt);
                    SelectedPrompt = newPrompt;
                }
                else
                {
                    var idx = -1;
                    for (int i = 0; i < AllPrompts.Count; i++)
                        if (AllPrompts[i].Id == EditingPromptId) { idx = i; break; }
                    if (idx >= 0)
                    {
                        var updated = new LlmPromptViewModel(
                            EditingPromptId, EditingPromptName, EditingPromptText, isBuiltIn: false);
                        AllPrompts[idx] = updated;
                        SelectedPrompt = updated;
                    }
                }
                IsEditingPrompt = false;
            });

            CancelEditCommand = new RelayCommand(() => IsEditingPrompt = false);

            AddSubstitutionCommand = new RelayCommand(() =>
                CustomSubstitutions.Add(new SubstitutionRuleViewModel()));

            RemoveSubstitutionCommand = new RelayCommand<SubstitutionRuleViewModel?>(r =>
            {
                if (r is not null) CustomSubstitutions.Remove(r);
            });

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
            return 0;
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

            // Clean Mode
            _settings.CleanHotkey = CleanHotkey;
            _settings.PostProcessingEnabled = PostProcessingEnabled;
            _settings.PostProcessingModel = PostProcessingModel;
            _settings.ActivePostProcessingPromptId = SelectedPrompt?.Id;

            // Prompts (user-defined only; built-ins are hard-coded stubs)
            _settings.PostProcessingPrompts = AllPrompts
                .Where(p => p.IsUserDefined)
                .Select(p => new LlmPrompt { Id = p.Id, Name = p.Name, Prompt = p.Prompt, IsBuiltIn = false })
                .ToList();

            // Dictionary
            _settings.CustomSubstitutions = CustomSubstitutions
                .Select(r => new TextSubstitution { Match = r.Match, Replace = r.Replace })
                .ToList();

            // Behavior additions — convert display strings to enums
            _settings.AutoSubmitKey = AutoSubmitKey switch
            {
                "Enter"      => Hush.Core.Configuration.AutoSubmitKey.Enter,
                "Ctrl+Enter" => Hush.Core.Configuration.AutoSubmitKey.CtrlEnter,
                _            => Hush.Core.Configuration.AutoSubmitKey.None,
            };
            _settings.ModelUnloadTimeout = ModelUnloadTimeout switch
            {
                "After 2 min"  => Hush.Core.Configuration.ModelUnloadTimeout.Min2,
                "After 5 min"  => Hush.Core.Configuration.ModelUnloadTimeout.Min5,
                "After 15 min" => Hush.Core.Configuration.ModelUnloadTimeout.Min15,
                _              => Hush.Core.Configuration.ModelUnloadTimeout.Never,
            };
        }
    }

    // Display view-models

    public sealed class LlmPromptViewModel(string id, string name, string prompt, bool isBuiltIn)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Prompt { get; } = prompt;
        public bool IsBuiltIn { get; } = isBuiltIn;
        public bool IsUserDefined => !IsBuiltIn;
    }

    public sealed partial class SubstitutionRuleViewModel : ObservableObject
    {
        [ObservableProperty] private string _match = string.Empty;
        [ObservableProperty] private string _replace = string.Empty;
    }

    public sealed record MicrophoneDevice(int DeviceIndex, string Name)
    {
        public override string ToString() => Name;
    }

}

