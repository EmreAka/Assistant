using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Assistant.Api.Features.Tefas.Models;

namespace Assistant.Api.Features.Tefas.Services;

public partial class TefasHtmlParser : ITefasHtmlParser
{
    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TefasParseResult Parse(
        string html,
        string requestedCode,
        string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new TefasParseResult(false, null, "TEFAS response was empty.");
        }

        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        var normalizedRequestedCode = requestedCode.Trim().ToUpperInvariant();
        var profileTable = document.QuerySelector("#MainContent_DetailsViewFund");
        var profileValues = ExtractProfileValues(profileTable);
        var code = GetNonEmptyValue(profileValues, "Kodu");

        if (string.IsNullOrWhiteSpace(code)
            || document.Body?.TextContent.Contains("Lütfen bir fon seçiniz.", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new TefasParseResult(false, null, $"Fund code '{normalizedRequestedCode}' was not found on TEFAS.");
        }

        var fundName = NormalizeWhitespace(document.QuerySelector("h2")?.TextContent);
        if (string.IsNullOrWhiteSpace(fundName) || fundName.Equals("Fon", StringComparison.OrdinalIgnoreCase))
        {
            return new TefasParseResult(false, null, $"Fund code '{normalizedRequestedCode}' did not resolve to a fund name.");
        }

        var summaryMetrics = ExtractListValues(document.QuerySelectorAll("li"));
        var priceScript = FindScriptContent(document, "chartMainContent_FonFiyatGrafik");
        var priceCategories = DeserializeStringArray(ExtractBracketedSection(priceScript, "\"categories\":"));
        var comparisonScript = FindScriptContent(document, "chartMainContent_ColumnChartMatch");
        var allocationScript = FindScriptContent(document, "chartMainContent_PieChartFonDagilim");

        var ranking = ParseRanking(GetNonEmptyValue(summaryMetrics, "Son Bir Yıllık Kategori Derecesi"));

        var fund = new TefasFundSnapshot(
            Code: code,
            Name: fundName,
            LastPriceTl: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Son Fiyat (TL)")),
            DailyReturnPercent: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Günlük Getiri (%)")),
            ShareCount: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Pay (Adet)")),
            TotalFundValueTl: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Fon Toplam Değer (TL)")),
            Category: GetNonEmptyValue(summaryMetrics, "Kategorisi"),
            OneYearCategoryRank: ranking.rank,
            CategoryFundCount: ranking.total,
            InvestorCount: ParseLong(GetNonEmptyValue(summaryMetrics, "Yatırımcı Sayısı (Kişi)")),
            MarketSharePercent: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Pazar Payı")),
            OneMonthReturnPercent: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Son 1 Ay Getirisi")),
            ThreeMonthReturnPercent: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Son 3 Ay Getirisi")),
            SixMonthReturnPercent: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Son 6 Ay Getirisi")),
            OneYearReturnPercent: ParseTefasDecimal(GetNonEmptyValue(summaryMetrics, "Son 1 Yıl Getirisi")),
            LastDataDate: ParseDate(priceCategories.LastOrDefault()),
            Profile: new TefasFundProfile(
                IsinCode: GetNonEmptyValue(profileValues, "ISIN Kodu"),
                PlatformTradingStatus: GetNonEmptyValue(profileValues, "Platform İşlem Durumu"),
                TradingStartTime: GetNonEmptyValue(profileValues, "İşlem Başlangıç Saati"),
                TradingEndTime: GetNonEmptyValue(profileValues, "Son İşlem Saati"),
                BuyValuation: GetNonEmptyValue(profileValues, "Fon Alış Valörü"),
                SellValuation: GetNonEmptyValue(profileValues, "Fon Satış Valörü"),
                MinBuyAmount: ParseTefasDecimal(GetNonEmptyValue(profileValues, "Min. Alış İşlem Miktarı")),
                MinSellAmount: ParseTefasDecimal(GetNonEmptyValue(profileValues, "Min. Satış İşlem Miktarı")),
                MaxBuyAmount: ParseTefasDecimal(GetNonEmptyValue(profileValues, "Max. Alış İşlem Miktarı")),
                MaxSellAmount: ParseTefasDecimal(GetNonEmptyValue(profileValues, "Max. Satış İşlem Miktarı")),
                EntryCommission: GetNonEmptyValue(profileValues, "Giriş Komisyonu"),
                ExitCommission: GetNonEmptyValue(profileValues, "Çıkış Komisyonu"),
                InterestContent: GetNonEmptyValue(profileValues, "Fonun Faiz İçeriği"),
                RiskValue: ParseInt(GetNonEmptyValue(profileValues, "Fonun Risk Değeri")),
                KapUrl: profileTable?.QuerySelector("a")?.GetAttribute("href")),
            AssetAllocations: ExtractAssetAllocations(allocationScript),
            OneYearComparisons: ExtractComparisonItems(comparisonScript),
            SourceUrl: sourceUrl);

        return new TefasParseResult(true, fund);
    }

    private static Dictionary<string, string> ExtractProfileValues(AngleSharp.Dom.IElement? table)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (table is null)
        {
            return values;
        }

        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("td").ToArray();
            if (cells.Length < 2)
            {
                continue;
            }

            var label = NormalizeWhitespace(cells[0].TextContent);
            var value = NormalizeWhitespace(cells[1].TextContent);
            if (!string.IsNullOrWhiteSpace(label))
            {
                values[label] = value;
            }
        }

        return values;
    }

    private static Dictionary<string, string> ExtractListValues(IEnumerable<AngleSharp.Dom.IElement> items)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var spans = item.QuerySelectorAll("span")
                .Select(x => NormalizeWhitespace(x.TextContent))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (spans.Length == 0)
            {
                continue;
            }

            var value = spans[^1];
            var fullText = NormalizeWhitespace(item.TextContent);
            if (string.IsNullOrWhiteSpace(fullText))
            {
                continue;
            }

            var label = fullText[..Math.Max(0, fullText.Length - value.Length)].Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            values[label] = value;
        }

        return values;
    }

    private static IReadOnlyList<TefasAssetAllocation> ExtractAssetAllocations(string? scriptContent)
    {
        var json = ExtractBracketedSection(scriptContent, "\"data\":");
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var rawItems = JsonSerializer.Deserialize<List<List<JsonElement>>>(json, JsonOptions) ?? [];
        return rawItems
            .Where(x => x.Count >= 2 && x[0].ValueKind == JsonValueKind.String)
            .Select(x => new TefasAssetAllocation(
                x[0].GetString() ?? string.Empty,
                x[1].GetDecimal()))
            .ToArray();
    }

    private static IReadOnlyList<TefasComparisonItem> ExtractComparisonItems(string? scriptContent)
    {
        var json = ExtractBracketedSection(scriptContent, "series:");
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var series = JsonSerializer.Deserialize<List<ComparisonSeriesDto>>(json, JsonOptions) ?? [];
        return series
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Id) && x.Data.Count > 0)
            .Select(x => new TefasComparisonItem(
                x.Name!,
                x.Id!,
                x.Data[0]))
            .ToArray();
    }

    private static string? FindScriptContent(AngleSharp.Dom.IDocument document, string marker)
    {
        var script = document.Scripts
            .Select(x => x.TextContent)
            .FirstOrDefault(x => x.Contains(marker, StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var markerIndex = script.IndexOf(marker, StringComparison.Ordinal);
        return markerIndex >= 0 ? script[markerIndex..] : script;
    }

    private static string? ExtractBracketedSection(string? source, string marker)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var startIndex = source.IndexOf('[', markerIndex);
        if (startIndex < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        for (var i = startIndex; i < source.Length; i++)
        {
            var current = source[i];
            var previous = i > 0 ? source[i - 1] : '\0';

            if (current == '"' && previous != '\\')
            {
                inString = !inString;
            }

            if (inString)
            {
                continue;
            }

            if (current == '[')
            {
                depth++;
            }
            else if (current == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return source[startIndex..(i + 1)];
                }
            }
        }

        return null;
    }

    private static string[] DeserializeStringArray(string? json)
    {
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
    }

    private static (int? rank, int? total) ParseRanking(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return (null, null);
        }

        return (ParseInt(parts[0]), ParseInt(parts[1]));
    }

    private static DateOnly? ParseDate(string? value)
    {
        return DateOnly.TryParseExact(value, "dd.MM.yyyy", TrCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? ParseTefasDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = value
            .Replace("%", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(",", ".", StringComparison.Ordinal)
            .Replace("\u00a0", string.Empty, StringComparison.Ordinal)
            .Trim();

        return decimal.TryParse(sanitized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? ParseInt(string? value)
    {
        var parsed = ParseTefasDecimal(value);
        return parsed.HasValue ? decimal.ToInt32(parsed.Value) : null;
    }

    private static long? ParseLong(string? value)
    {
        var parsed = ParseTefasDecimal(value);
        return parsed.HasValue ? decimal.ToInt64(parsed.Value) : null;
    }

    private static string? GetNonEmptyValue(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed class ComparisonSeriesDto
    {
        public string? Name { get; init; }
        public string? Id { get; init; }
        public List<decimal> Data { get; init; } = [];
    }
}
