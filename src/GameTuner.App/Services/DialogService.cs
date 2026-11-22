using Microsoft.UI.Xaml.Controls;

namespace GameTuner.App.Services;

/// <summary>
/// Lets view models ask the user something without referencing UI classes.
/// (A view model that creates a ContentDialog itself can't be unit-tested.)
/// </summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string text, string confirmText = "Да");
    Task ShowAsync(string title, string text);
}

public sealed class DialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string text, string confirmText = "Да")
    {
        var dialog = Create(title, text);
        dialog.PrimaryButtonText = confirmText;
        dialog.CloseButtonText = "Отмена";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowAsync(string title, string text)
    {
        var dialog = Create(title, text);
        dialog.CloseButtonText = "OK";
        await dialog.ShowAsync();
    }

    private static ContentDialog Create(string title, string text) => new()
    {
        Title = title,
        Content = new TextBlock { Text = text, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
        // A ContentDialog must know which window it belongs to.
        XamlRoot = App.MainWindow?.Content.XamlRoot
    };
}
