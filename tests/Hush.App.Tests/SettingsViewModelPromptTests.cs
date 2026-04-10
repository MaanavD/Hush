// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.App.ViewModels;
using Hush.Core.Configuration;

namespace Hush.App.Tests;

/// <summary>
/// Tests for prompt management in <see cref="SettingsViewModel"/>.
/// </summary>
public sealed class SettingsViewModelPromptTests
{
    private static SettingsViewModel MakeVm(HushSettings? settings = null)
        => new(settings ?? new HushSettings());

    [Fact]
    public void AddPrompt_SetsIsEditingPromptTrue()
    {
        var vm = MakeVm();

        vm.AddPromptCommand.Execute(null);

        Assert.True(vm.IsEditingPrompt);
        Assert.Null(vm.EditingPromptId);
        Assert.Equal(string.Empty, vm.EditingPromptName);
        Assert.Equal(string.Empty, vm.EditingPromptText);
    }

    [Fact]
    public void SavePrompt_WithNullId_AddsToAllPrompts()
    {
        var vm = MakeVm();
        int initialCount = vm.AllPrompts.Count;

        vm.AddPromptCommand.Execute(null);
        vm.EditingPromptName = "My Prompt";
        vm.EditingPromptText = "Do something useful.";
        vm.SavePromptCommand.Execute(null);

        Assert.Equal(initialCount + 1, vm.AllPrompts.Count);
        Assert.False(vm.IsEditingPrompt);

        var added = vm.AllPrompts.Last();
        Assert.Equal("My Prompt", added.Name);
        Assert.Equal("Do something useful.", added.Prompt);
        Assert.True(added.IsUserDefined);
    }

    [Fact]
    public void SavePrompt_WithExistingId_UpdatesExistingEntry()
    {
        var vm = MakeVm();

        // Add a user prompt first
        vm.AddPromptCommand.Execute(null);
        vm.EditingPromptName = "Original Name";
        vm.EditingPromptText = "Original text.";
        vm.SavePromptCommand.Execute(null);

        var added = vm.AllPrompts.Last();
        int countBefore = vm.AllPrompts.Count;

        // Edit it
        vm.EditPromptCommand.Execute(added);
        Assert.True(vm.IsEditingPrompt);
        Assert.Equal("Original Name", vm.EditingPromptName);

        vm.EditingPromptName = "Updated Name";
        vm.EditingPromptText = "Updated text.";
        vm.SavePromptCommand.Execute(null);

        // Count unchanged, entry updated
        Assert.Equal(countBefore, vm.AllPrompts.Count);
        Assert.False(vm.IsEditingPrompt);

        var updated = vm.AllPrompts.First(p => p.Id == added.Id);
        Assert.Equal("Updated Name", updated.Name);
        Assert.Equal("Updated text.", updated.Prompt);
    }

    [Fact]
    public void DeletePrompt_BuiltIn_DoesNotRemove()
    {
        var vm = MakeVm();
        var builtIn = vm.AllPrompts.First(p => p.IsBuiltIn);
        int countBefore = vm.AllPrompts.Count;

        vm.DeletePromptCommand.Execute(builtIn);

        Assert.Equal(countBefore, vm.AllPrompts.Count);
        Assert.Contains(builtIn, vm.AllPrompts);
    }

    [Fact]
    public void DeletePrompt_UserDefined_Removes()
    {
        var vm = MakeVm();

        // Add a user prompt
        vm.AddPromptCommand.Execute(null);
        vm.EditingPromptName = "To Delete";
        vm.EditingPromptText = "Some prompt.";
        vm.SavePromptCommand.Execute(null);

        var toDelete = vm.AllPrompts.First(p => p.IsUserDefined && p.Name == "To Delete");
        int countBefore = vm.AllPrompts.Count;

        vm.DeletePromptCommand.Execute(toDelete);

        Assert.Equal(countBefore - 1, vm.AllPrompts.Count);
        Assert.DoesNotContain(toDelete, vm.AllPrompts);
    }

    [Fact]
    public void CancelEdit_SetsIsEditingPromptFalse()
    {
        var vm = MakeVm();

        vm.AddPromptCommand.Execute(null);
        Assert.True(vm.IsEditingPrompt);

        vm.CancelEditCommand.Execute(null);

        Assert.False(vm.IsEditingPrompt);
    }

    [Fact]
    public void SelectedPrompt_DefaultsToFirstBuiltIn()
    {
        // No active prompt ID set — should default to first built-in
        var vm = MakeVm(new HushSettings { ActivePostProcessingPromptId = null });

        Assert.NotNull(vm.SelectedPrompt);
        Assert.True(vm.SelectedPrompt!.IsBuiltIn);
        // First built-in stub is "fix-punctuation"
        Assert.Equal("fix-punctuation", vm.SelectedPrompt.Id);
    }
}
