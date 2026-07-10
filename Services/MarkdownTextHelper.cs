using System.Text.RegularExpressions;

namespace UnturnedModLoader.Services;

public static partial class MarkdownTextHelper
{
    public static string StripForPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        normalized = FencedCodeBlockRegex().Replace(normalized, "");
        normalized = InlineCodeRegex().Replace(normalized, "");
        normalized = ImageRegex().Replace(normalized, "$1");
        normalized = LinkRegex().Replace(normalized, "$1");
        normalized = HeadingRegex().Replace(normalized, "");
        normalized = BoldRegex().Replace(normalized, "$2");
        normalized = ItalicRegex().Replace(normalized, "$2");
        normalized = StrikethroughRegex().Replace(normalized, "$1");
        normalized = QuoteRegex().Replace(normalized, "");
        normalized = UnorderedListRegex().Replace(normalized, "");
        normalized = OrderedListRegex().Replace(normalized, "");
        normalized = normalized.Replace('|', ' ');

        return CollapseWhitespaceRegex().Replace(normalized, " ").Trim();
    }

    [GeneratedRegex("```[\\s\\S]*?```", RegexOptions.Multiline)]
    private static partial Regex FencedCodeBlockRegex();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex("!\\[([^\\]]*)\\]\\([^)]*\\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex("\\[([^\\]]*)\\]\\([^)]*\\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex("^#{1,6}\\s+", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex("(\\*\\*|__)(.*?)\\1")]
    private static partial Regex BoldRegex();

    [GeneratedRegex("(\\*|_)(.*?)\\1")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex("~~(.*?)~~")]
    private static partial Regex StrikethroughRegex();

    [GeneratedRegex("^>\\s+", RegexOptions.Multiline)]
    private static partial Regex QuoteRegex();

    [GeneratedRegex("^[-*+]\\s+", RegexOptions.Multiline)]
    private static partial Regex UnorderedListRegex();

    [GeneratedRegex("^\\d+\\.\\s+", RegexOptions.Multiline)]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex("\\s{2,}")]
    private static partial Regex CollapseWhitespaceRegex();
}