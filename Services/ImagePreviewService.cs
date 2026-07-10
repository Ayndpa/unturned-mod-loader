using Avalonia.Controls;
using Avalonia.Media.Imaging;
using UnturnedModLoader.Views;

namespace UnturnedModLoader.Services;

public static class ImagePreviewService
{
    public static void Show(Window? owner, Bitmap image, string? title = null)
    {
        var window = new ImagePreviewWindow(image, title);
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
    }
}