using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using UnturnedModLoader.I18n;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.Views;

public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow()
    {
        InitializeComponent();
    }

    public ImagePreviewWindow(Bitmap image, string? title = null)
        : this()
    {
        PreviewImage.Source = image;

        var displayTitle = string.IsNullOrWhiteSpace(title)
            ? L.Get(ImagePreview.Title)
            : title;

        Title = displayTitle;
        TitleText.Text = displayTitle;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}