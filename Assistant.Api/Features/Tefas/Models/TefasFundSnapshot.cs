namespace Assistant.Api.Features.Tefas.Models;

public sealed record TefasFundSnapshot(
    string Code,
    string Name,
    decimal? LastPriceTl,
    decimal? DailyReturnPercent,
    decimal? ShareCount,
    decimal? TotalFundValueTl,
    string? Category,
    int? OneYearCategoryRank,
    int? CategoryFundCount,
    long? InvestorCount,
    decimal? MarketSharePercent,
    decimal? OneMonthReturnPercent,
    decimal? ThreeMonthReturnPercent,
    decimal? SixMonthReturnPercent,
    decimal? OneYearReturnPercent,
    DateOnly? LastDataDate,
    TefasFundProfile Profile,
    IReadOnlyList<TefasAssetAllocation> AssetAllocations,
    IReadOnlyList<TefasComparisonItem> OneYearComparisons,
    string SourceUrl);

public sealed record TefasFundProfile(
    string? IsinCode,
    string? PlatformTradingStatus,
    string? TradingStartTime,
    string? TradingEndTime,
    string? BuyValuation,
    string? SellValuation,
    decimal? MinBuyAmount,
    decimal? MinSellAmount,
    decimal? MaxBuyAmount,
    decimal? MaxSellAmount,
    string? EntryCommission,
    string? ExitCommission,
    string? InterestContent,
    int? RiskValue,
    string? KapUrl);

public sealed record TefasAssetAllocation(string AssetType, decimal WeightPercent);

public sealed record TefasComparisonItem(string Name, string BenchmarkId, decimal ReturnPercent);

public sealed record TefasParseResult(bool Found, TefasFundSnapshot? Fund, string? FailureReason = null);
