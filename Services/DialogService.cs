using Avalonia.Controls;
using UnturnedModLoader.ViewModels;
using UnturnedModLoader.Views;

namespace UnturnedModLoader.Services;

public static class DialogService
{
    public static async Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        bool useMarkdown = false)
    {
        var viewModel = new ConfirmDialogViewModel(title, message, confirmText, cancelText, useMarkdown);
        var dialog = new ConfirmDialogWindow { DataContext = viewModel };
        var result = false;

        viewModel.CloseRequested += value =>
        {
            result = value;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
