using System.Net;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.Tefas.Models;
using Assistant.Api.Features.Tefas.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Api.Tests.Tefas;

public class TefasAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_UsesFallback_WhenAgentFails()
    {
        var snapshot = new TefasFundSnapshot(
            Code: "AFT",
            Name: "AK PORTFÖY YENİ TEKNOLOJİLER YABANCI HİSSE SENEDİ FONU",
            LastPriceTl: 0.838944m,
            DailyReturnPercent: -0.1552m,
            ShareCount: 29247232572m,
            TotalFundValueTl: 24536779779.29m,
            Category: "Hisse Senedi Fonu",
            OneYearCategoryRank: 7,
            CategoryFundCount: 184,
            InvestorCount: 147531,
            MarketSharePercent: 12.82m,
            OneMonthReturnPercent: 2.408656m,
            ThreeMonthReturnPercent: 1.570021m,
            SixMonthReturnPercent: 10.489571m,
            OneYearReturnPercent: 66.577449m,
            LastDataDate: new DateOnly(2026, 3, 18),
            Profile: new TefasFundProfile(
                IsinCode: "TRYAKBK00144",
                PlatformTradingStatus: "TEFAS'ta işlem görüyor",
                TradingStartTime: "09:00",
                TradingEndTime: "17:45",
                BuyValuation: "1",
                SellValuation: "2",
                MinBuyAmount: 1,
                MinSellAmount: 1,
                MaxBuyAmount: 99000000000m,
                MaxSellAmount: 90000000000m,
                EntryCommission: null,
                ExitCommission: null,
                InterestContent: null,
                RiskValue: 6,
                KapUrl: "https://www.kap.org.tr/tr/fon-bilgileri/genel/aft-ak-portfoy-yeni-teknolojiler-yabanci-hisse-senedi-fonu"),
            AssetAllocations:
            [
                new TefasAssetAllocation("Yabancı Hisse Senedi", 99.15m),
                new TefasAssetAllocation("Mevduat (TL)", 0.05m)
            ],
            OneYearComparisons:
            [
                new TefasComparisonItem("AFT", "AK PORTFÖY YENİ TEKNOLOJİLER YABANCI HİSSE SENEDİ FONU", 66.58m),
                new TefasComparisonItem("ALTIN", "ALTIN", 91.52m)
            ],
            SourceUrl: "https://www.tefas.gov.tr/FonAnaliz.aspx?FonKod=AFT");

        var service = new TefasAnalysisService(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler())),
            new StubTefasHtmlParser(new TefasParseResult(true, snapshot)),
            new ThrowingAgentService(),
            NullLogger<TefasAnalysisService>.Instance);

        var response = await service.AnalyzeAsync(42, "aft", CancellationToken.None);

        Assert.True(response.Found);
        Assert.Contains("AFT", response.UserMessage);
        Assert.Contains("18.03.2026", response.UserMessage);
        Assert.Contains("yatırım tavsiyesi değildir", response.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingAgentService : IAgentService
    {
        public Task<string> RunAsync(long chatId, string userInput, string? systemInstructionsAugmentation = null, IEnumerable<AITool>? additionalTools = null, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("agent unavailable");
        }
    }

    private sealed class StubTefasHtmlParser(TefasParseResult result) : ITefasHtmlParser
    {
        public TefasParseResult Parse(string html, string requestedCode, string sourceUrl)
        {
            return result;
        }
    }

    private sealed class StubHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            httpClient.BaseAddress ??= new Uri("https://www.tefas.gov.tr/");
            return httpClient;
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>")
            });
        }
    }
}
