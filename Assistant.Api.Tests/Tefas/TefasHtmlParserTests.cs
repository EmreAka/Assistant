using Assistant.Api.Features.Tefas.Services;

namespace Assistant.Api.Tests.Tefas;

public class TefasHtmlParserTests
{
    private readonly TefasHtmlParser _parser = new();

    [Fact]
    public void Parse_ReturnsExpectedSnapshot_ForValidFixture()
    {
        var html = LoadFixture("aft.html");

        var result = _parser.Parse(
            html,
            "AFT",
            "https://www.tefas.gov.tr/FonAnaliz.aspx?FonKod=AFT");

        Assert.True(result.Found);
        Assert.NotNull(result.Fund);
        Assert.Equal("AFT", result.Fund!.Code);
        Assert.Equal("AK PORTFÖY YENİ TEKNOLOJİLER YABANCI HİSSE SENEDİ FONU", result.Fund.Name);
        Assert.Equal(0.838944m, result.Fund.LastPriceTl);
        Assert.Equal(66.577449m, result.Fund.OneYearReturnPercent);
        Assert.Equal(6, result.Fund.Profile.RiskValue);
        Assert.Equal(new DateOnly(2026, 3, 18), result.Fund.LastDataDate);
        Assert.Equal(4, result.Fund.AssetAllocations.Count);
        Assert.Equal("Yabancı Hisse Senedi", result.Fund.AssetAllocations[0].AssetType);
        Assert.Equal(99.15m, result.Fund.AssetAllocations[0].WeightPercent);
        Assert.Contains(result.Fund.OneYearComparisons, x => x.Name == "AFT" && x.ReturnPercent == 66.58m);
    }

    [Fact]
    public void Parse_ReturnsNotFound_ForInvalidFixture()
    {
        var html = LoadFixture("invalid.html");

        var result = _parser.Parse(
            html,
            "ZZZ",
            "https://www.tefas.gov.tr/FonAnaliz.aspx?FonKod=ZZZ");

        Assert.False(result.Found);
        Assert.Null(result.Fund);
    }

    private static string LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tefas", name);
        return File.ReadAllText(path);
    }
}
