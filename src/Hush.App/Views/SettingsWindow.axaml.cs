// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Hush.App.ViewModels;

namespace Hush.App.Views;

/// <summary>Settings window code-behind. Applies user changes on save and closes the dialog.</summary>
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.Apply();
        Close();
    }
}
