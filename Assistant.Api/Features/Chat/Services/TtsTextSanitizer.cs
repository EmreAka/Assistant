using System.Text.RegularExpressions;

namespace Assistant.Api.Features.Chat.Services;

public static partial class TtsTextSanitizer
{
    public static string Sanitize(string text)
    {
        var sanitized = MarkdownLinkPattern().Replace(text, "$1");
        sanitized = CodeFencePattern().Replace(sanitized, " ");
        sanitized = HeadingOrQuotePrefixPattern().Replace(sanitized, "");
        sanitized = BulletPrefixPattern().Replace(sanitized, "");
        sanitized = MarkdownFormattingCharsPattern().Replace(sanitized, "");
        sanitized = EmojiPattern().Replace(sanitized, "");
        sanitized = WhitespacePattern().Replace(sanitized, " ");

        return sanitized.Trim();
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex("```.*?```", RegexOptions.Singleline)]
    private static partial Regex CodeFencePattern();

    [GeneratedRegex(@"^[ \t]*[>#]+[ \t]*", RegexOptions.Multiline)]
    private static partial Regex HeadingOrQuotePrefixPattern();

    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+", RegexOptions.Multiline)]
    private static partial Regex BulletPrefixPattern();

    [GeneratedRegex(@"[*_~`]")]
    private static partial Regex MarkdownFormattingCharsPattern();

    // BMP symbol blocks that are almost exclusively emoji/pictographs (arrows, misc technical,
    // enclosed alphanumerics, dingbats, supplemental arrows-B, misc symbols and arrows),
    // plus ZWJ, variation selector-16, combining enclosing keycap, and the surrogate-pair
    // emoji blocks that live outside the BMP (U+1F300-U+1FAFF etc).
    [GeneratedRegex(
        "[←-⇿⌀-⏿①-⓿☀-➿⤀-⥿⬀-⯿‍⃣️]" +
        "|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDD00-\uDFFF]")]
    private static partial Regex EmojiPattern();

    [GeneratedRegex(@"[ \t]*\r?\n[ \t]*|[ \t]{2,}")]
    private static partial Regex WhitespacePattern();
}
