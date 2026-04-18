using System.Globalization;
using System.Text.Json;
using Assistant.Api.Extensions;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.Tefas.Models;

namespace Assistant.Api.Features.Tefas.Services;

public class TefasAnalysisService(
    IHttpClientFactory httpClientFactory,
    ITefasHtmlParser tefasHtmlParser,
    IAgentService agentService,
    ILogger<TefasAnalysisService> logger
) : ITefasAnalysisService
{
    private static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");

    public async Task<TefasAnalysisResponse> AnalyzeAsync(
        long chatId,
        string fundCode,
        CancellationToken cancellationToken)
    {
        var normalizedCode = fundCode.Trim().ToUpperInvariant();
        var sourceUrl = $"FonAnaliz.aspx?FonKod={Uri.EscapeDataString(normalizedCode)}";

        using var httpClient = httpClientFactory.CreateClient(BotServiceRegistration.TefasHttpClientName);
        using var response = await httpClient.GetAsync(sourceUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var parseResult = tefasHtmlParser.Parse(
            html,
            normalizedCode,
            new Uri(httpClient.BaseAddress!, sourceUrl).ToString());

        if (!parseResult.Found || parseResult.Fund is null)
        {
            return new TefasAnalysisResponse(
                false,
                $"{normalizedCode} için TEFAS'ta bir fon bulamadım.");
        }

        var fund = parseResult.Fund;

        try
        {
            var agentResponse = await agentService.RunAsync(
                chatId,
                BuildAgentInput(fund),
                systemInstructionsAugmentation: BuildTefasAugmentation(),
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(agentResponse))
            {
                return new TefasAnalysisResponse(true, agentResponse.Trim(), fund);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent TEFAS analysis failed for fund {FundCode}. Falling back to deterministic summary.", normalizedCode);
        }

        return new TefasAnalysisResponse(true, BuildFallbackSummary(fund), fund);
    }

    private static string BuildAgentInput(TefasFundSnapshot fund)
    {
        var payload = new
        {
            source = "TEFAS",
            fundCode = fund.Code,
            fundName = fund.Name,
            dataDate = fund.LastDataDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            sourceUrl = fund.SourceUrl,
            summary = new
            {
                lastPriceTl = fund.LastPriceTl,
                dailyReturnPercent = fund.DailyReturnPercent,
                shareCount = fund.ShareCount,
                totalFundValueTl = fund.TotalFundValueTl,
                category = fund.Category,
                oneYearCategoryRank = fund.OneYearCategoryRank,
                categoryFundCount = fund.CategoryFundCount,
                investorCount = fund.InvestorCount,
                marketSharePercent = fund.MarketSharePercent,
                oneMonthReturnPercent = fund.OneMonthReturnPercent,
                threeMonthReturnPercent = fund.ThreeMonthReturnPercent,
                sixMonthReturnPercent = fund.SixMonthReturnPercent,
                oneYearReturnPercent = fund.OneYearReturnPercent
            },
            profile = new
            {
                isinCode = fund.Profile.IsinCode,
                platformTradingStatus = fund.Profile.PlatformTradingStatus,
                tradingStartTime = fund.Profile.TradingStartTime,
                tradingEndTime = fund.Profile.TradingEndTime,
                buyValuation = fund.Profile.BuyValuation,
                sellValuation = fund.Profile.SellValuation,
                minBuyAmount = fund.Profile.MinBuyAmount,
                minSellAmount = fund.Profile.MinSellAmount,
                maxBuyAmount = fund.Profile.MaxBuyAmount,
                maxSellAmount = fund.Profile.MaxSellAmount,
                entryCommission = fund.Profile.EntryCommission,
                exitCommission = fund.Profile.ExitCommission,
                interestContent = fund.Profile.InterestContent,
                riskValue = fund.Profile.RiskValue,
                kapUrl = fund.Profile.KapUrl
            },
            assetAllocation = fund.AssetAllocations,
            oneYearComparison = fund.OneYearComparisons
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

        return $"""
                 Review this TEFAS fund snapshot and write a short natural summary for the user.
                 Use only this structured data:

                 {json}
                 """;
    }

    private static string BuildTefasAugmentation()
    {
        return """

               YOU ARE NOW HANDLING A TEFAS FUND ANALYSIS REQUEST.
               Use the TEFAS snapshot in the user message as the primary source of truth.
               Do not browse the web or introduce outside facts unless the provided TEFAS data explicitly includes them.
               Do not invent missing figures, dates, holdings, fees, or risk commentary.
               Focus on the most useful observations: recent performance, one-year positioning, risk level, investor interest, and asset concentration.
               Keep the answer brief and natural.
               If you mention data freshness, use the exact TEFAS date provided in the snapshot instead of relative date phrases.
               """;
    }

    private static string BuildFallbackSummary(TefasFundSnapshot fund)
    {
        var lines = new List<string>
        {
            $"{fund.Name} ({fund.Code}) için {FormatDate(fund.LastDataDate)} tarihli TEFAS özeti:",
            $"Son fiyat {FormatDecimal(fund.LastPriceTl)} TL, günlük getiri {FormatPercent(fund.DailyReturnPercent)}.",
            $"Getiriler: 1 ay {FormatPercent(fund.OneMonthReturnPercent)}, 3 ay {FormatPercent(fund.ThreeMonthReturnPercent)}, 6 ay {FormatPercent(fund.SixMonthReturnPercent)}, 1 yıl {FormatPercent(fund.OneYearReturnPercent)}.",
            $"Kategori: {fund.Category ?? "bilinmiyor"}. Risk değeri: {fund.Profile.RiskValue?.ToString(CultureInfo.InvariantCulture) ?? "bilinmiyor"}, yatırımcı sayısı: {FormatWholeNumber(fund.InvestorCount)}, pazar payı: {FormatPercent(fund.MarketSharePercent)}."
        };

        var topAsset = fund.AssetAllocations
            .OrderByDescending(x => x.WeightPercent)
            .FirstOrDefault();
        if (topAsset is not null)
        {
            lines.Add($"Portföyde en yüksek ağırlık {topAsset.AssetType} tarafında ve payı {FormatPercent(topAsset.WeightPercent)}.");
        }

        var leadingBenchmark = fund.OneYearComparisons
            .Where(x => !x.Name.Equals(fund.Code, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.ReturnPercent)
            .FirstOrDefault();
        if (leadingBenchmark is not null)
        {
            lines.Add($"TEFAS 1 yıllık karşılaştırmada öne çıkan göstergelerden {leadingBenchmark.Name} getirisi {FormatPercent(leadingBenchmark.ReturnPercent)} seviyesinde.");
        }

        lines.Add("Bu özet yalnızca TEFAS verisine dayanır; yatırım tavsiyesi değildir.");
        return string.Join("\n", lines);
    }

    private static string FormatDate(DateOnly? value)
    {
        return value?.ToString("dd.MM.yyyy", TrCulture) ?? "bilinmeyen";
    }

    private static string FormatDecimal(decimal? value)
    {
        return value?.ToString("N6", TrCulture) ?? "bilinmiyor";
    }

    private static string FormatPercent(decimal? value)
    {
        return value.HasValue
            ? $"%{value.Value.ToString("N2", TrCulture)}"
            : "bilinmiyor";
    }

    private static string FormatWholeNumber(long? value)
    {
        return value?.ToString("N0", TrCulture) ?? "bilinmiyor";
    }
}
