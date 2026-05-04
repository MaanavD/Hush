// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hush.App.ViewModels;

namespace Hush.App.Views;

/// <summary>Settings window code-behind. Applies user changes on save and closes the dialog.</summary>
public sealed partial class SettingsWindow : Window
{
    public Func<Task<bool>>? SaveRequested { get; set; }

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public async Task<bool> ConfirmModelDownloadAsync(LanguageModelOptionViewModel model)
    {
        var dialog = new Window
        {
            Title = "Download cleaning model?",
            Width = 430,
            Height = 230,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dialog.Content = BuildDownloadPromptContent(model, dialog);

        var result = await dialog.ShowDialog<bool?>(this);
        return result == true;
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (SaveRequested is not null && !await SaveRequested())
            return;

        Close();
    }

    private static Control BuildDownloadPromptContent(LanguageModelOptionViewModel model, Window dialog)
    {
        var message = $"{model.Alias} is not cached locally. Download {model.DownloadSizeLabel} "
            + "and start preparing clean mode after saving?";

        var downloadButton = new Button
        {
            Content = "Download",
            Padding = new Avalonia.Thickness(16, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Avalonia.Thickness(16, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        downloadButton.Click += (_, _) =>
        {
            dialog.Close(true);
        };
        cancelButton.Click += (_, _) =>
        {
            dialog.Close(false);
        };

        return new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "Download this model now?",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = message,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.78,
                },
                new TextBlock
                {
                    Text = "Progress will appear in the Hush overlay while the model downloads and loads.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        cancelButton,
                        downloadButton,
                    },
                },
            },
        };
    }
}
