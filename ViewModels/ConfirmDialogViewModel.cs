using CommunityToolkit.Mvvm.Input;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.ViewModels;

public partial class ConfirmDialogViewModel : ViewModelBase
{
    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public bool IsMarkdown { get; }

    public event Action<bool>? CloseRequested;

    public ConfirmDialogViewModel(
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        bool isMarkdown = false)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText ?? L.Get(Common.Confirm);
        CancelText = cancelText ?? L.Get(Common.Cancel);
        IsMarkdown = isMarkdown;
    }

    [RelayCommand]
    private void Confirm() => CloseRequested?.Invoke(true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
