using System.Threading.Tasks;
using Avalonia.Controls;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Services;
using UnturnedModLoader.ViewModels;

namespace UnturnedModLoader.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is MainViewModel viewModel)
        {
            if (viewModel.CheckIfGameIsRunning())
            {
                e.Cancel = true;
                _ = ShowGameRunningWarningAsync();
            }
        }
    }

    private async Task ShowGameRunningWarningAsync()
    {
        await DialogService.ConfirmAsync(
            this,
            L.Get(Common.Confirm),
            L.Get(Main.GameRunningCloseBlocked),
            confirmText: L.Get(Common.Confirm),
            cancelText: ""
        );
    }
}