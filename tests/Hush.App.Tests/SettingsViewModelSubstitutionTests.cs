// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.App.ViewModels;
using Hush.Core.Configuration;

namespace Hush.App.Tests;

/// <summary>
/// Tests for dictionary (substitution) management and Apply() serialization
/// in <see cref="SettingsViewModel"/>.
/// </summary>
public sealed class SettingsViewModelSubstitutionTests
{
    private static SettingsViewModel MakeVm(HushSettings? settings = null)
        => new(settings ?? new HushSettings());

    [Fact]
    public void AddSubstitution_AddsEmptyRuleToCollection()
    {
        var vm = MakeVm();
        int initialCount = vm.CustomSubstitutions.Count;

        vm.AddSubstitutionCommand.Execute(null);

        Assert.Equal(initialCount + 1, vm.CustomSubstitutions.Count);
        var rule = vm.CustomSubstitutions.Last();
        Assert.Equal(string.Empty, rule.Match);
        Assert.Equal(string.Empty, rule.Replace);
    }

    [Fact]
    public void RemoveSubstitution_RemovesFromCollection()
    {
        var vm = MakeVm();

        vm.AddSubstitutionCommand.Execute(null);
        var rule = vm.CustomSubstitutions.Last();
        int countBefore = vm.CustomSubstitutions.Count;

        vm.RemoveSubstitutionCommand.Execute(rule);

        Assert.Equal(countBefore - 1, vm.CustomSubstitutions.Count);
        Assert.DoesNotContain(rule, vm.CustomSubstitutions);
    }

    [Fact]
    public void Apply_SerializesSubstitutionsToSettings()
    {
        var settings = new HushSettings();
        var vm = MakeVm(settings);

        vm.AddSubstitutionCommand.Execute(null);
        vm.CustomSubstitutions[0].Match = "foundry local";
        vm.CustomSubstitutions[0].Replace = "Foundry Local";

        vm.Apply();

        Assert.Single(settings.CustomSubstitutions);
        Assert.Equal("foundry local", settings.CustomSubstitutions[0].Match);
        Assert.Equal("Foundry Local", settings.CustomSubstitutions[0].Replace);
    }

    [Fact]
    public void Apply_SerializesUserPromptsToSettings()
    {
        var settings = new HushSettings();
        var vm = MakeVm(settings);

        // Add a user-defined prompt
        vm.AddPromptCommand.Execute(null);
        vm.EditingPromptName = "My Custom Prompt";
        vm.EditingPromptText = "Rewrite this nicely.";
        vm.SavePromptCommand.Execute(null);

        vm.Apply();

        // Only user-defined prompts are persisted; built-ins are excluded
        var userPrompts = settings.PostProcessingPrompts;
        Assert.Single(userPrompts);
        Assert.Equal("My Custom Prompt", userPrompts[0].Name);
        Assert.Equal("Rewrite this nicely.", userPrompts[0].Prompt);
    }
}
