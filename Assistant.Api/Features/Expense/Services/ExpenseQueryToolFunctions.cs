using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Assistant.Api.Data;
using Microsoft.EntityFrameworkCore;
using ExpenseModel = Assistant.Api.Features.Expense.Models.Expense;

namespace Assistant.Api.Features.Expense.Services;

public record ExpenseQueryItem(
    string ExpenseDate,
    decimal Amount,
    string Currency,
    string Description
);

public record ExpenseAggregateRow(
    string GroupKey,
    int TransactionCount,
    decimal TotalAmount,
    decimal AverageAmount,
    string FirstExpenseDate,
    string LastExpenseDate
);

public record ExpenseQueryResponse(
    bool IsSuccess,
    string Message,
    string SummaryMode,
    string GroupBy,
    int TransactionCount,
    decimal TotalAmount,
    decimal AverageAmount,
    string? StartDate,
    string? EndDate,
    List<ExpenseQueryItem>? Items,
    List<ExpenseAggregateRow>? Groups
);

public class ExpenseQueryToolFunctions(
    long chatId,
    ApplicationDbContext dbContext,
    ILogger<ExpenseQueryToolFunctions> logger
)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Description("Queries the current user's expense records with safe read-only filters. Use this tool before answering spending questions. It can return a limited transaction list or aggregated summaries grouped by day, month, or description.")]
    public async Task<string> QueryExpenses(
        [Description("Start date in ISO format yyyy-MM-dd. Leave null when no lower date bound is needed.")] string? startDate = null,
        [Description("End date in ISO format yyyy-MM-dd. Leave null when no upper date bound is needed.")] string? endDate = null,
        [Description("Optional case-insensitive text to match within the expense description.")] string? searchText = null,
        [Description("Optional minimum amount filter in TRY.")] decimal? minAmount = null,
        [Description("Optional maximum amount filter in TRY.")] decimal? maxAmount = null,
        [Description("Maximum number of rows or groups to return. Use a small value unless the user explicitly asks for more detail.")] int? limit = null,
        [Description("Sort order. Allowed values: date_desc, date_asc, amount_desc, amount_asc, total_desc, total_asc.")] string? sortBy = null,
        [Description("Grouping mode. Allowed values: none, day, month, description.")] string? groupBy = null,
        [Description("Response mode. Allowed values: list or aggregate.")] string? summaryMode = null)
    {
        try
        {
            var userId = await dbContext.TelegramUsers
                .AsNoTracking()
                .Where(x => x.ChatId == chatId)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(CancellationToken.None);

            if (!userId.HasValue)
            {
                return Serialize(new ExpenseQueryResponse(
                    false,
                    "User not found for this chat.",
                    "list",
                    "none",
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null));
            }

            if (!TryParseDate(startDate, out var parsedStart, out var startError))
            {
                return SerializeError(startError!);
            }

            if (!TryParseDate(endDate, out var parsedEnd, out var endError))
            {
                return SerializeError(endError!);
            }

            if (parsedStart.HasValue && parsedEnd.HasValue && parsedStart > parsedEnd)
            {
                return SerializeError("startDate cannot be after endDate.");
            }

            var normalizedGroupBy = NormalizeGroupBy(groupBy);
            var normalizedSummaryMode = NormalizeSummaryMode(summaryMode);
            var normalizedSortBy = NormalizeSortBy(sortBy, normalizedSummaryMode);
            var normalizedLimit = Math.Clamp(limit ?? 20, 1, 100);

            IQueryable<ExpenseModel> query = dbContext.Expenses
                .AsNoTracking()
                .Where(x => x.TelegramUserId == userId.Value);

            if (parsedStart.HasValue)
            {
                var startUtc = ToUtcDate(parsedStart.Value);
                query = query.Where(x => x.ExpenseDate >= startUtc);
            }

            if (parsedEnd.HasValue)
            {
                var endExclusiveUtc = ToUtcDate(parsedEnd.Value.AddDays(1));
                query = query.Where(x => x.ExpenseDate < endExclusiveUtc);
            }

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var trimmedSearch = searchText.Trim();
                query = query.Where(x => x.Description.Contains(trimmedSearch));
            }

            if (minAmount.HasValue)
            {
                query = query.Where(x => x.Amount >= minAmount.Value);
            }

            if (maxAmount.HasValue)
            {
                query = query.Where(x => x.Amount <= maxAmount.Value);
            }

            var baseSummary = await query
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    TransactionCount = g.Count(),
                    TotalAmount = g.Sum(x => x.Amount),
                    AverageAmount = g.Average(x => x.Amount)
                })
                .FirstOrDefaultAsync(CancellationToken.None);

            if (baseSummary is null)
            {
                return Serialize(new ExpenseQueryResponse(
                    true,
                    "No expense records matched the requested filters.",
                    normalizedSummaryMode,
                    normalizedGroupBy,
                    0,
                    0,
                    0,
                    parsedStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    parsedEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    [],
                    []));
            }

            if (normalizedSummaryMode == "aggregate")
            {
                var aggregateRows = await BuildAggregateRowsAsync(query, normalizedGroupBy, normalizedSortBy, normalizedLimit);

                return Serialize(new ExpenseQueryResponse(
                    true,
                    "Expense aggregate query completed.",
                    normalizedSummaryMode,
                    normalizedGroupBy,
                    baseSummary.TransactionCount,
                    baseSummary.TotalAmount,
                    baseSummary.AverageAmount,
                    parsedStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    parsedEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    null,
                    aggregateRows));
            }

            var items = await ApplyExpenseSort(query, normalizedSortBy)
                .Take(normalizedLimit)
                .Select(x => new ExpenseQueryItem(
                    x.ExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    x.Amount,
                    x.Currency,
                    x.Description))
                .ToListAsync(CancellationToken.None);

            return Serialize(new ExpenseQueryResponse(
                true,
                "Expense list query completed.",
                normalizedSummaryMode,
                normalizedGroupBy,
                baseSummary.TransactionCount,
                baseSummary.TotalAmount,
                baseSummary.AverageAmount,
                parsedStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                parsedEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                items,
                null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Expense query tool failed for ChatId: {ChatId}", chatId);
            return SerializeError($"Expense query failed: {ex.Message}");
        }
    }

    private static async Task<List<ExpenseAggregateRow>> BuildAggregateRowsAsync(
        IQueryable<ExpenseModel> query,
        string groupBy,
        string sortBy,
        int limit)
    {
        if (groupBy == "day")
        {
            var groupedQuery = query
                .GroupBy(x => x.ExpenseDate.Date)
                .Select(g => new
                {
                    GroupKey = g.Key,
                    Count = g.Count(),
                    TotalAmount = g.Sum(x => x.Amount),
                    AverageAmount = g.Average(x => x.Amount),
                    FirstExpenseDate = g.Min(x => x.ExpenseDate),
                    LastExpenseDate = g.Max(x => x.ExpenseDate)
                });

            var sortedQuery = sortBy switch
            {
                "total_asc" => groupedQuery.OrderBy(x => x.TotalAmount).ThenBy(x => x.GroupKey),
                "date_asc" => groupedQuery.OrderBy(x => x.GroupKey),
                "date_desc" => groupedQuery.OrderByDescending(x => x.GroupKey),
                _ => groupedQuery.OrderByDescending(x => x.TotalAmount).ThenBy(x => x.GroupKey)
            };

            var groups = await sortedQuery
                .Take(limit)
                .ToListAsync(CancellationToken.None);

            return groups
                .Select(x => new ExpenseAggregateRow(
                    x.GroupKey.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    x.Count,
                    x.TotalAmount,
                    x.AverageAmount,
                    x.FirstExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    x.LastExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .ToList();
        }

        if (groupBy == "month")
        {
            var groupedQuery = query
                .GroupBy(x => new { x.ExpenseDate.Year, x.ExpenseDate.Month })
                .Select(g => new
                {
                    GroupKey = new DateTime(g.Key.Year, g.Key.Month, 1),
                    Count = g.Count(),
                    TotalAmount = g.Sum(x => x.Amount),
                    AverageAmount = g.Average(x => x.Amount),
                    FirstExpenseDate = g.Min(x => x.ExpenseDate),
                    LastExpenseDate = g.Max(x => x.ExpenseDate)
                });

            var sortedQuery = sortBy switch
            {
                "total_asc" => groupedQuery.OrderBy(x => x.TotalAmount).ThenBy(x => x.GroupKey),
                "date_asc" => groupedQuery.OrderBy(x => x.GroupKey),
                "date_desc" => groupedQuery.OrderByDescending(x => x.GroupKey),
                _ => groupedQuery.OrderByDescending(x => x.TotalAmount).ThenBy(x => x.GroupKey)
            };

            var groups = await sortedQuery
                .Take(limit)
                .ToListAsync(CancellationToken.None);

            return groups
                .Select(x => new ExpenseAggregateRow(
                    x.GroupKey.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    x.Count,
                    x.TotalAmount,
                    x.AverageAmount,
                    x.FirstExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    x.LastExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .ToList();
        }

        var descriptionGroups = query
            .GroupBy(x => x.Description)
            .Select(g => new
            {
                GroupKey = g.Key,
                Count = g.Count(),
                TotalAmount = g.Sum(x => x.Amount),
                AverageAmount = g.Average(x => x.Amount),
                FirstExpenseDate = g.Min(x => x.ExpenseDate),
                LastExpenseDate = g.Max(x => x.ExpenseDate)
            });

        var sortedDescriptionQuery = sortBy switch
        {
            "total_asc" => descriptionGroups.OrderBy(x => x.TotalAmount).ThenBy(x => x.GroupKey),
            "date_asc" => descriptionGroups.OrderBy(x => x.GroupKey),
            "date_desc" => descriptionGroups.OrderByDescending(x => x.GroupKey),
            _ => descriptionGroups.OrderByDescending(x => x.TotalAmount).ThenBy(x => x.GroupKey)
        };

        var descriptionResults = await sortedDescriptionQuery
            .Take(limit)
            .ToListAsync(CancellationToken.None);

        return descriptionResults
            .Select(x => new ExpenseAggregateRow(
                x.GroupKey,
                x.Count,
                x.TotalAmount,
                x.AverageAmount,
                x.FirstExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                x.LastExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static IQueryable<ExpenseModel> ApplyExpenseSort(IQueryable<ExpenseModel> query, string sortBy)
    {
        return sortBy switch
        {
            "date_asc" => query.OrderBy(x => x.ExpenseDate).ThenBy(x => x.Id),
            "amount_desc" => query.OrderByDescending(x => x.Amount).ThenByDescending(x => x.ExpenseDate).ThenByDescending(x => x.Id),
            "amount_asc" => query.OrderBy(x => x.Amount).ThenBy(x => x.ExpenseDate).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.ExpenseDate).ThenByDescending(x => x.Id)
        };
    }

    private static string NormalizeGroupBy(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "day" => "day",
            "month" => "month",
            "description" => "description",
            _ => "none"
        };
    }

    private static string NormalizeSummaryMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "aggregate" => "aggregate",
            _ => "list"
        };
    }

    private static string NormalizeSortBy(string? value, string summaryMode)
    {
        var normalized = value?.Trim().ToLowerInvariant();

        if (summaryMode == "aggregate")
        {
            return normalized switch
            {
                "date_asc" => "date_asc",
                "date_desc" => "date_desc",
                "total_asc" => "total_asc",
                _ => "total_desc"
            };
        }

        return normalized switch
        {
            "date_asc" => "date_asc",
            "amount_desc" => "amount_desc",
            "amount_asc" => "amount_asc",
            _ => "date_desc"
        };
    }

    private static bool TryParseDate(string? value, out DateOnly? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            parsed = date;
            return true;
        }

        error = $"Invalid date '{value}'. Use yyyy-MM-dd.";
        return false;
    }

    private static DateTime ToUtcDate(DateOnly value)
    {
        return DateTime.SpecifyKind(value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
    }

    private static string Serialize(ExpenseQueryResponse response)
    {
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    private static string SerializeError(string message)
    {
        return Serialize(new ExpenseQueryResponse(
            false,
            message,
            "list",
            "none",
            0,
            0,
            0,
            null,
            null,
            null,
            null));
    }
}
