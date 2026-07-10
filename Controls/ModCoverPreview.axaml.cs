using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.Controls;

public partial class ModCoverPreview : UserControl
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ModCoverPreview, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<bool> HasImageProperty =
        AvaloniaProperty.Register<ModCoverPreview, bool>(nameof(HasImage));

    public static readonly StyledProperty<double> ThumbnailSizeProperty =
        AvaloniaProperty.Register<ModCoverPreview, double>(nameof(ThumbnailSize), 56d);

    public static readonly StyledProperty<double> HoverZoomSizeProperty =
        AvaloniaProperty.Register<ModCoverPreview, double>(nameof(HoverZoomSize), 240d);

    public static readonly StyledProperty<string?> PreviewTitleProperty =
        AvaloniaProperty.Register<ModCoverPreview, string?>(nameof(PreviewTitle));

    public static readonly StyledProperty<double> PlaceholderIconSizeProperty =
        AvaloniaProperty.Register<ModCoverPreview, double>(nameof(PlaceholderIconSize), 20d);

    private readonly ScaleTransform _coverScale = new(1, 1);
    private readonly DispatcherTimer _closePopupTimer;
    private bool _isHoveringPopup;

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool HasImage
    {
        get => GetValue(HasImageProperty);
        set => SetValue(HasImageProperty, value);
    }

    public double ThumbnailSize
    {
        get => GetValue(ThumbnailSizeProperty);
        set => SetValue(ThumbnailSizeProperty, value);
    }

    public double HoverZoomSize
    {
        get => GetValue(HoverZoomSizeProperty);
        set => SetValue(HoverZoomSizeProperty, value);
    }

    public string? PreviewTitle
    {
        get => GetValue(PreviewTitleProperty);
        set => SetValue(PreviewTitleProperty, value);
    }

    public double PlaceholderIconSize
    {
        get => GetValue(PlaceholderIconSizeProperty);
        set => SetValue(PlaceholderIconSizeProperty, value);
    }

    public ModCoverPreview()
    {
        InitializeComponent();
        CoverImage.RenderTransform = _coverScale;
        UpdateCoverCursor();
        HasImageProperty.Changed.AddClassHandler<ModCoverPreview>((control, _) => control.UpdateCoverCursor());
        SourceProperty.Changed.AddClassHandler<ModCoverPreview>((control, _) => control.UpdateCoverCursor());

        _closePopupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _closePopupTimer.Tick += (_, _) =>
        {
            _closePopupTimer.Stop();
            if (!_isHoveringPopup)
                HoverPopup.IsOpen = false;
        };
    }

    private void OnCoverPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!HasImage || Source is null)
            return;

        _closePopupTimer.Stop();
        HoverPopup.IsOpen = true;
        _coverScale.ScaleX = 1.06;
        _coverScale.ScaleY = 1.06;
        CoverHost.BorderBrush = Application.Current?.FindResource("InkSoftBrush") as IBrush
            ?? CoverHost.BorderBrush;
    }

    private void OnCoverPointerExited(object? sender, PointerEventArgs e)
    {
        ResetCoverScale();
        ScheduleClosePopup();
    }

    private void OnHoverPopupPointerEntered(object? sender, PointerEventArgs e)
    {
        _isHoveringPopup = true;
        _closePopupTimer.Stop();
    }

    private void OnHoverPopupPointerExited(object? sender, PointerEventArgs e)
    {
        _isHoveringPopup = false;
        ScheduleClosePopup();
    }

    private void OnCoverPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!HasImage || Source is null)
            return;

        e.Handled = true;
        HoverPopup.IsOpen = false;
        ResetCoverScale();

        var owner = TopLevel.GetTopLevel(this) as Window;
        ImagePreviewService.Show(owner, Source, PreviewTitle);
    }

    private void ScheduleClosePopup()
    {
        if (_isHoveringPopup)
            return;

        _closePopupTimer.Stop();
        _closePopupTimer.Start();
    }

    private void ResetCoverScale()
    {
        _coverScale.ScaleX = 1;
        _coverScale.ScaleY = 1;
        CoverHost.BorderBrush = Application.Current?.FindResource("HairlineBrush") as IBrush
            ?? CoverHost.BorderBrush;
    }

    private void UpdateCoverCursor() =>
        CoverHost.Cursor = HasImage && Source is not null
            ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
}