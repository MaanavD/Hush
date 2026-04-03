using Avalonia.Controls;
using Avalonia.Interactivity;
using Hush.App.ViewModels;

namespace Hush.App.Views;

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
