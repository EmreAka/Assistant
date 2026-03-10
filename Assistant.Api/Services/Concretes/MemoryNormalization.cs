namespace Assistant.Api.Services.Concretes;

internal static class MemoryNormalization
{
    public static string NormalizeContent(string content)
    {
        return string.Join(
            " ",
            content
                .Trim()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string NormalizeCategory(string category)
    {
        var trimmed = category.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(trimmed) ? "fact" : trimmed;
    }
}
