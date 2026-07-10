using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UnturnedModLoader.Services;

namespace UnturnedModLoader.Controls;

public class MarkdownContentControl : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownContentControl, string?>(nameof(Markdown));

    public static readonly StyledProperty<IBrush?> ForegroundBrushProperty =
        AvaloniaProperty.Register<MarkdownContentControl, IBrush?>(nameof(ForegroundBrush));

    public static readonly StyledProperty<IBrush?> MutedForegroundBrushProperty =
        AvaloniaProperty.Register<MarkdownContentControl, IBrush?>(nameof(MutedForegroundBrush));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public IBrush? ForegroundBrush
    {
        get => GetValue(ForegroundBrushProperty);
        set => SetValue(ForegroundBrushProperty, value);
    }

    public IBrush? MutedForegroundBrush
    {
        get => GetValue(MutedForegroundBrushProperty);
        set => SetValue(MutedForegroundBrushProperty, value);
    }

    static MarkdownContentControl()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownContentControl>((control, _) => control.UpdateContent());
        ForegroundBrushProperty.Changed.AddClassHandler<MarkdownContentControl>((control, _) => control.UpdateContent());
        MutedForegroundBrushProperty.Changed.AddClassHandler<MarkdownContentControl>((control, _) => control.UpdateContent());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateContent();
    }

    private void UpdateContent()
    {
        Content = MarkdownRenderer.Build(Markdown, ForegroundBrush, MutedForegroundBrush);
    }
}