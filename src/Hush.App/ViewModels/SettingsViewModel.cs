// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hush.Core.Audio;
using Hush.Core.Configuration;
using Hush.Core.Models;
using Hush.Core.PostProcessing;

namespace Hush.App.ViewModels
{
    /// <summary>
    /// View-model for the Settings window.
    /// </summary>
    public sealed partial class SettingsViewModel : ObservableObject
    {
        private readonly HushSettings _settings;
        private readonly ILanguageModelCatalogService? _languageModelCatalog;
        private bool _syncingSelectedLanguageModel;

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
        [ObservableProperty] private string _cleanHotkey = "Ctrl+H";
        [ObservableProperty] private bool _postProcessingEnabled = true;
        [ObservableProperty] private string _postProcessingModel = "qwen3-0.6b";
        [ObservableProperty] private LanguageModelOptionViewModel? _selectedPostProcessingLanguageModel;
        [ObservableProperty] private bool _isRefreshingLanguageModels;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLanguageModelCatalogStatus))]
        private string _languageModelCatalogStatus = string.Empty;
        [ObservableProperty] private string? _activePostProcessingPromptId;

        public ObservableCollection<LanguageModelOptionViewModel> AvailableLanguageModels { get; } = new();
        public bool HasLanguageModelCatalogStatus => !string.IsNullOrWhiteSpace(LanguageModelCatalogStatus);

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

        public SettingsViewModel(
            HushSettings settings,
            ILanguageModelCatalogService? languageModelCatalog = null)
        {
            _settings = settings;
            _languageModelCatalog = languageModelCatalog;

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
            SelectLanguageModel(_postProcessingModel);

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
                AllPrompts.Add(new LlmPromptViewModel(p.Id, p.Name, p.Prompt, isBuiltIn: true, p.Icon, p.Description));
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

            // Seed with System Default only; full enumeration happens lazily
            // when the settings dialog opens (TrayIcon calls RefreshMicrophones).
            // This keeps unit tests from invoking PortAudio native code.
            AvailableMicrophones.Add(new MicrophoneDevice(AudioCaptureService.SystemDefaultDeviceNumber, "System Default"));
            _selectedMicrophoneIndex = FindMicIndex(settings.MicrophoneDeviceIndex);
        }

        public void RefreshMicrophones()
        {
            AvailableMicrophones.Clear();
            foreach (var (index, name) in AudioCaptureService.GetAvailableDevices())
                AvailableMicrophones.Add(new MicrophoneDevice(index, name));
        }

        public async Task RefreshLanguageModelsAsync(CancellationToken cancellationToken = default)
        {
            if (_languageModelCatalog is null || IsRefreshingLanguageModels)
                return;

            var selectedAlias = PostProcessingModel;
            IsRefreshingLanguageModels = true;
            LanguageModelCatalogStatus = "Loading language models...";

            try
            {
                var models = await _languageModelCatalog.ListSmallLanguageModelsAsync(cancellationToken);
                AvailableLanguageModels.Clear();

                foreach (var model in models)
                    AvailableLanguageModels.Add(LanguageModelOptionViewModel.FromCatalog(model));

                SelectLanguageModel(selectedAlias);
                LanguageModelCatalogStatus = models.Count == 0
                    ? "No compatible language models found."
                    : $"{models.Count} language models available.";
            }
            catch (OperationCanceledException)
            {
                LanguageModelCatalogStatus = "Model catalog refresh canceled.";
                SelectLanguageModel(selectedAlias);
            }
            catch (Exception)
            {
                LanguageModelCatalogStatus = "Could not load Foundry Local model catalog.";
                SelectLanguageModel(selectedAlias);
            }
            finally
            {
                IsRefreshingLanguageModels = false;
            }
        }

        partial void OnPostProcessingModelChanged(string value)
        {
            if (!_syncingSelectedLanguageModel)
                SelectLanguageModel(value);
        }

        partial void OnSelectedPostProcessingLanguageModelChanged(LanguageModelOptionViewModel? value)
        {
            if (value is null || _syncingSelectedLanguageModel)
                return;

            _syncingSelectedLanguageModel = true;
            try
            {
                PostProcessingModel = value.Alias;
            }
            finally
            {
                _syncingSelectedLanguageModel = false;
            }
        }

        private void SelectLanguageModel(string? alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
                return;

            var option = AvailableLanguageModels.FirstOrDefault(
                model => string.Equals(model.Alias, alias, StringComparison.OrdinalIgnoreCase));

            if (option is null)
            {
                option = LanguageModelOptionViewModel.Custom(alias.Trim());
                AvailableLanguageModels.Insert(0, option);
            }

            _syncingSelectedLanguageModel = true;
            try
            {
                SelectedPostProcessingLanguageModel = option;
            }
            finally
            {
                _syncingSelectedLanguageModel = false;
            }
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

    public sealed class LlmPromptViewModel(string id, string name, string prompt, bool isBuiltIn, string icon = "", string description = "")
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Prompt { get; } = prompt;
        public bool IsBuiltIn { get; } = isBuiltIn;
        public bool IsUserDefined => !IsBuiltIn;
        public string Icon { get; } = string.IsNullOrEmpty(icon) && !isBuiltIn ? "✨" : icon;
        public string Description { get; } = string.IsNullOrEmpty(description) && !isBuiltIn ? "Custom prompt" : description;
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

    public sealed record LanguageModelOptionViewModel(string Alias, string Detail, bool IsCatalogModel)
    {
        public static LanguageModelOptionViewModel FromCatalog(LanguageModelCatalogItem model)
        {
            var cacheLabel = model.IsCached ? "cached" : model.FileSizeLabel;
            return new LanguageModelOptionViewModel(
                model.Alias,
                $"{model.ParameterLabel} - {cacheLabel}",
                IsCatalogModel: true);
        }

        public static LanguageModelOptionViewModel Custom(string alias)
            => new(alias, "Current custom alias", IsCatalogModel: false);

        public override string ToString() => Alias;
    }

}

