using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace UnturnedModLoader.Services;

public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static Control Build(string? markdown, IBrush? foreground = null, IBrush? mutedForeground = null)
    {
        foreground ??= Brushes.Black;
        mutedForeground ??= Brushes.Gray;

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new TextBlock
            {
                Text = "",
                Foreground = mutedForeground,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        var document = Markdown.Parse(markdown, Pipeline);
        var panel = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (var block in document)
        {
            var rendered = RenderBlock(block, foreground, mutedForeground);
            if (rendered is not null)
                panel.Children.Add(rendered);
        }

        if (panel.Children.Count == 0)
        {
            return new TextBlock
            {
                Text = markdown,
                Foreground = foreground,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            };
        }

        return panel;
    }

    private static Control? RenderBlock(Block block, IBrush foreground, IBrush mutedForeground) =>
        block switch
        {
            HeadingBlock heading => RenderHeading(heading, foreground),
            ParagraphBlock paragraph => RenderParagraph(paragraph, foreground),
            ListBlock list => RenderList(list, foreground, mutedForeground),
            QuoteBlock quote => RenderQuote(quote, foreground, mutedForeground),
            FencedCodeBlock fenced => RenderCodeBlock(fenced.Lines.ToString(), foreground, mutedForeground),
            CodeBlock code => RenderCodeBlock(code.Lines.ToString(), foreground, mutedForeground),
            ThematicBreakBlock => RenderSeparator(mutedForeground),
            Table table => RenderTable(table, foreground, mutedForeground),
            _ => null,
        };

    private static Control RenderHeading(HeadingBlock heading, IBrush foreground)
    {
        var text = GetInlineText(heading.Inline);
        var fontSize = heading.Level switch
        {
            1 => 20d,
            2 => 17d,
            3 => 15d,
            _ => 14d,
        };

        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, heading.Level <= 2 ? 4 : 0, 0, 0),
        };
    }

    private static Control RenderParagraph(ParagraphBlock paragraph, IBrush foreground)
    {
        var textBlock = CreateTextBlock(foreground);
        AppendInlines(textBlock.Inlines!, paragraph.Inline, foreground);
        return textBlock;
    }

    private static Control RenderList(ListBlock list, IBrush foreground, IBrush mutedForeground)
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var index = 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
                continue;

            var itemPanel = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var prefix = list.IsOrdered ? $"{index}." : "•";
            index++;

            foreach (var block in listItem)
            {
                if (block is ParagraphBlock paragraph)
                {
                    // Grid (Auto + *) so list text measures against remaining width and wraps.
                    // Horizontal StackPanel gives infinite width, so text never wraps and gets clipped.
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };

                    var prefixBlock = new TextBlock
                    {
                        Text = prefix,
                        Foreground = mutedForeground,
                        FontSize = 13,
                        MinWidth = 18,
                        Margin = new Thickness(0, 0, 8, 0),
                    };
                    Grid.SetColumn(prefixBlock, 0);

                    var textBlock = CreateTextBlock(foreground);
                    AppendInlines(textBlock.Inlines!, paragraph.Inline, foreground);
                    Grid.SetColumn(textBlock, 1);

                    row.Children.Add(prefixBlock);
                    row.Children.Add(textBlock);
                    itemPanel.Children.Add(row);
                    prefix = "";
                }
                else
                {
                    var nested = RenderBlock(block, foreground, mutedForeground);
                    if (nested is not null)
                    {
                        nested.Margin = new Thickness(26, 0, 0, 0);
                        itemPanel.Children.Add(nested);
                    }
                }
            }

            panel.Children.Add(itemPanel);
        }

        return panel;
    }

    private static Control RenderQuote(QuoteBlock quote, IBrush foreground, IBrush mutedForeground)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var block in quote)
        {
            var rendered = RenderBlock(block, foreground, mutedForeground);
            if (rendered is not null)
                content.Children.Add(rendered);
        }

        return new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = mutedForeground,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        };
    }

    private static Control RenderCodeBlock(string? code, IBrush foreground, IBrush mutedForeground)
    {
        return new Border
        {
            Background = CreateCodeBackgroundBrush(),
            BorderBrush = mutedForeground,
            BorderThickness = new Thickness(1),
            Opacity = 0.95,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = code?.TrimEnd() ?? "",
                FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                FontSize = 12,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            },
        };
    }

    private static IBrush CreateCodeBackgroundBrush() =>
        new SolidColorBrush(Color.FromArgb(28, 120, 120, 120));

    private static Control RenderSeparator(IBrush mutedForeground) =>
        new Border
        {
            Height = 1,
            Background = mutedForeground,
            Opacity = 0.35,
            Margin = new Thickness(0, 4),
        };

    private static Control RenderTable(Table table, IBrush foreground, IBrush mutedForeground)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", table.ColumnDefinitions.Count))),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", table.Count))),
            ShowGridLines = false,
        };

        for (var rowIndex = 0; rowIndex < table.Count; rowIndex++)
        {
            if (table[rowIndex] is not TableRow row)
                continue;

            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                if (row[columnIndex] is not TableCell cell)
                    continue;

                var cellText = string.Join(
                    "\n",
                    cell.OfType<ParagraphBlock>().Select(p => GetInlineText(p.Inline)));

                var textBlock = new TextBlock
                {
                    Text = cellText,
                    Foreground = foreground,
                    FontSize = 12,
                    FontWeight = rowIndex == 0 && table.ColumnDefinitions.Count > 0
                        ? FontWeight.SemiBold
                        : FontWeight.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 6),
                };

                Grid.SetRow(textBlock, rowIndex);
                Grid.SetColumn(textBlock, columnIndex);
                grid.Children.Add(textBlock);
            }
        }

        return new Border
        {
            BorderBrush = mutedForeground,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Opacity = 0.95,
            Child = grid,
        };
    }

    private static TextBlock CreateTextBlock(IBrush foreground) =>
        new()
        {
            FontSize = 13,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

    private static void AppendInlines(
        InlineCollection inlines,
        ContainerInline? container,
        IBrush foreground)
    {
        if (container is null)
            return;

        foreach (var inline in container)
            AppendInline(inlines, inline, foreground);
    }

    private static void AppendInline(InlineCollection inlines, Markdig.Syntax.Inlines.Inline inline, IBrush foreground)
    {
        switch (inline)
        {
            case LiteralInline literal:
                inlines.Add(new Run(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis when emphasis.DelimiterCount >= 2:
                var bold = new Run(GetInlineText(emphasis));
                bold.FontWeight = FontWeight.SemiBold;
                inlines.Add(bold);
                break;

            case EmphasisInline emphasis:
                var italic = new Run(GetInlineText(emphasis));
                italic.FontStyle = FontStyle.Italic;
                inlines.Add(italic);
                break;

            case CodeInline code:
                inlines.Add(new Run(code.Content)
                {
                    FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                    Background = CreateCodeBackgroundBrush(),
                });
                break;

            case LinkInline link:
                var linkText = GetInlineText(link);
                var linkLabel = string.IsNullOrWhiteSpace(linkText) ? link.Url ?? "" : linkText;
                var linkBlock = new TextBlock
                {
                    Text = linkLabel,
                    TextDecorations = TextDecorations.Underline,
                    Foreground = foreground,
                    FontSize = 13,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };

                if (!string.IsNullOrWhiteSpace(link.Url))
                    linkBlock.PointerPressed += (_, _) => OpenUrl(link.Url);

                inlines.Add(new InlineUIContainer { Child = linkBlock });
                break;

            case LineBreakInline:
                inlines.Add(new Run(Environment.NewLine));
                break;

            case ContainerInline container:
                AppendInlines(inlines, container, foreground);
                break;
        }
    }

    private static string GetInlineText(ContainerInline? container)
    {
        if (container is null)
            return "";

        using var writer = new StringWriter();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    writer.Write(literal.Content);
                    break;
                case CodeInline code:
                    writer.Write(code.Content);
                    break;
                case LineBreakInline:
                    writer.Write(' ');
                    break;
                case ContainerInline nested:
                    writer.Write(GetInlineText(nested));
                    break;
            }
        }

        return writer.ToString();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore failures opening external links.
        }
    }
}