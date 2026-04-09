using Assistant.Api.Data;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Features.UserManagement.Services;

public class MemoryService(
    ApplicationDbContext dbContext,
    ILogger<MemoryService> logger
) : IMemoryService
{
    private const int ExpiredTimeBoundMemoryLookbackDays = 30;
    private const int ExpiredTimeBoundMemoryLimit = 10;
    private const int MaximumMemorySearchTerms = 8;
    private const int MinimumSearchTermLength = 2;

    public async Task<IReadOnlyList<UserMemory>> GetActiveMemoriesAsync(long chatId, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;

        return await dbContext.UserMemories
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId)
            .Where(x => x.ExpiresAt == null || x.ExpiresAt > nowUtc)
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserMemory>> GetRecentExpiredTimeBoundMemoriesAsync(long chatId, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var expiredTimeBoundCutoffUtc = nowUtc.AddDays(-ExpiredTimeBoundMemoryLookbackDays);

        return await dbContext.UserMemories
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId)
            .Where(x => x.ExpiresAt != null && x.ExpiresAt <= nowUtc && x.ExpiresAt >= expiredTimeBoundCutoffUtc)
            .OrderByDescending(x => x.ExpiresAt)
            .ThenByDescending(x => x.Importance)
            .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
            .Take(ExpiredTimeBoundMemoryLimit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserMemory>> SearchActiveMemoriesAsync(
        long chatId,
        string? query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (maxResults <= 0)
        {
            return [];
        }

        var tsQuery = BuildTsQuery(query);
        if (IsNpgsqlProvider() && !string.IsNullOrWhiteSpace(tsQuery))
        {
            return await dbContext.UserMemories
                .AsNoTracking()
                .Where(x => x.TelegramUser.ChatId == chatId)
                .Where(x => x.ExpiresAt == null || x.ExpiresAt > DateTime.UtcNow)
                .Where(x => x.SearchVector.Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
                .Select(x => new
                {
                    Memory = x,
                    Score = x.SearchVector.RankCoverDensity(EF.Functions.ToTsQuery("simple", tsQuery))
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Memory.Importance)
                .ThenByDescending(x => x.Memory.LastUsedAt ?? x.Memory.CreatedAt)
                .Take(maxResults)
                .Select(x => x.Memory)
                .ToListAsync(cancellationToken);
        }

        var activeMemories = await GetActiveMemoriesAsync(chatId, cancellationToken);
        return RankMemories(activeMemories, query, maxResults, fallbackToDefaultOrderWhenQueryMissing: true);
    }

    public async Task<IReadOnlyList<UserMemory>> SearchRecentExpiredTimeBoundMemoriesAsync(
        long chatId,
        string? query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (maxResults <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var tsQuery = BuildTsQuery(query);
        if (IsNpgsqlProvider() && !string.IsNullOrWhiteSpace(tsQuery))
        {
            var nowUtc = DateTime.UtcNow;
            var expiredTimeBoundCutoffUtc = nowUtc.AddDays(-ExpiredTimeBoundMemoryLookbackDays);

            return await dbContext.UserMemories
                .AsNoTracking()
                .Where(x => x.TelegramUser.ChatId == chatId)
                .Where(x => x.ExpiresAt != null && x.ExpiresAt <= nowUtc && x.ExpiresAt >= expiredTimeBoundCutoffUtc)
                .Where(x => x.SearchVector.Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
                .Select(x => new
                {
                    Memory = x,
                    Score = x.SearchVector.RankCoverDensity(EF.Functions.ToTsQuery("simple", tsQuery))
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Memory.ExpiresAt)
                .ThenByDescending(x => x.Memory.Importance)
                .Take(maxResults)
                .Select(x => x.Memory)
                .ToListAsync(cancellationToken);
        }

        var expiredMemories = await GetRecentExpiredTimeBoundMemoriesAsync(chatId, cancellationToken);
        return RankMemories(expiredMemories, query, maxResults, fallbackToDefaultOrderWhenQueryMissing: false);
    }

    public async Task<IReadOnlyList<UserMemory>> ListMemoriesAsync(
        long chatId,
        string statusFilter,
        string? searchText,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (maxResults <= 0)
        {
            return [];
        }

        var normalizedFilter = NormalizeStatusFilter(statusFilter);
        var nowUtc = DateTime.UtcNow;

        var memoriesQuery = dbContext.UserMemories
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId);

        memoriesQuery = normalizedFilter switch
        {
            "expired" => memoriesQuery.Where(x => x.ExpiresAt != null && x.ExpiresAt <= nowUtc),
            "active" => memoriesQuery.Where(x => x.ExpiresAt == null || x.ExpiresAt > nowUtc),
            _ => memoriesQuery
        };

        var tsQuery = BuildTsQuery(searchText);
        if (IsNpgsqlProvider() && !string.IsNullOrWhiteSpace(tsQuery))
        {
            return await memoriesQuery
                .Where(x => x.SearchVector.Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
                .Select(x => new
                {
                    Memory = x,
                    Score = x.SearchVector.RankCoverDensity(EF.Functions.ToTsQuery("simple", tsQuery))
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Memory.Importance)
                .ThenByDescending(x => x.Memory.LastUsedAt ?? x.Memory.CreatedAt)
                .Take(maxResults)
                .Select(x => x.Memory)
                .ToListAsync(cancellationToken);
        }

        var memories = await memoriesQuery.ToListAsync(cancellationToken);
        return RankMemories(memories, searchText, maxResults, fallbackToDefaultOrderWhenQueryMissing: true);
    }

    public async Task<bool> SaveMemoryAsync(
        long chatId,
        string content,
        string category,
        int importance,
        DateTime? expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var normalizedContent = NormalizeContent(content);
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return false;
        }

        var userId = await dbContext.TelegramUsers
            .AsNoTracking()
            .Where(x => x.ChatId == chatId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId == null)
        {
            logger.LogWarning("Memory could not be saved because telegram user was not found. ChatId: {ChatId}", chatId);
            return false;
        }

        var normalizedCategory = NormalizeCategory(category);
        var normalizedImportance = Math.Clamp(importance, 1, 10);

        var existingMemory = await dbContext.UserMemories
            .FirstOrDefaultAsync(
                x => x.TelegramUserId == userId.Value
                    && x.Category == normalizedCategory
                    && x.Content == normalizedContent,
                cancellationToken);

        if (existingMemory is not null)
        {
            existingMemory.Importance = Math.Max(existingMemory.Importance, normalizedImportance);
            existingMemory.LastUsedAt = DateTime.UtcNow;
            if (expiresAtUtc.HasValue)
            {
                existingMemory.ExpiresAt = expiresAtUtc.Value;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        dbContext.UserMemories.Add(new UserMemory
        {
            TelegramUserId = userId.Value,
            Category = normalizedCategory,
            Content = normalizedContent,
            Importance = normalizedImportance,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            ExpiresAt = expiresAtUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<UserMemory?> UpdateMemoryAsync(
        long chatId,
        int memoryId,
        string? content,
        string? category,
        int? importance,
        DateTime? expiresAtUtc,
        bool clearExpiration,
        CancellationToken cancellationToken)
    {
        var memory = await dbContext.UserMemories
            .FirstOrDefaultAsync(
                x => x.Id == memoryId && x.TelegramUser.ChatId == chatId,
                cancellationToken);

        if (memory is null)
        {
            return null;
        }

        if (content is not null)
        {
            var normalizedContent = NormalizeContent(content);
            if (string.IsNullOrWhiteSpace(normalizedContent))
            {
                return null;
            }

            memory.Content = normalizedContent;
        }

        if (category is not null)
        {
            memory.Category = NormalizeCategory(category);
        }

        if (importance.HasValue)
        {
            memory.Importance = Math.Clamp(importance.Value, 1, 10);
        }

        if (clearExpiration)
        {
            memory.ExpiresAt = null;
        }
        else if (expiresAtUtc.HasValue)
        {
            memory.ExpiresAt = expiresAtUtc.Value;
        }

        memory.LastUsedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return memory;
    }

    public async Task<bool> DeleteMemoryAsync(long chatId, int memoryId, CancellationToken cancellationToken)
    {
        var memory = await dbContext.UserMemories
            .FirstOrDefaultAsync(
                x => x.Id == memoryId && x.TelegramUser.ChatId == chatId,
                cancellationToken);

        if (memory is null)
        {
            return false;
        }

        dbContext.UserMemories.Remove(memory);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task TouchMemoriesAsync(IEnumerable<int> memoryIds, CancellationToken cancellationToken)
    {
        var ids = memoryIds
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
        {
            return;
        }

        var memories = await dbContext.UserMemories
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);

        if (memories.Count == 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        foreach (var memory in memories)
        {
            memory.LastUsedAt = nowUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeContent(string content)
    {
        return string.Join(
            " ",
            content
                .Trim()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeCategory(string category)
    {
        var trimmed = category.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(trimmed) ? "fact" : trimmed;
    }

    private static string NormalizeStatusFilter(string? statusFilter)
    {
        var normalized = statusFilter?.Trim().ToLowerInvariant();
        return normalized is "active" or "expired" or "all"
            ? normalized
            : "active";
    }

    private bool IsNpgsqlProvider()
    {
        return string.Equals(dbContext.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);
    }

    private static string? BuildTsQuery(string? value)
    {
        var terms = Tokenize(value);
        if (terms.Length == 0)
        {
            return null;
        }

        return string.Join(
            " | ",
            terms.Select(term => term.Length >= 4 ? $"{term}:*" : term));
    }

    private static IReadOnlyList<UserMemory> RankMemories(
        IReadOnlyList<UserMemory> memories,
        string? query,
        int maxResults,
        bool fallbackToDefaultOrderWhenQueryMissing)
    {
        if (memories.Count == 0 || maxResults <= 0)
        {
            return [];
        }

        var terms = Tokenize(query);
        if (terms.Length == 0)
        {
            if (!fallbackToDefaultOrderWhenQueryMissing)
            {
                return [];
            }

            return memories
                .OrderByDescending(x => x.Importance)
                .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
                .Take(maxResults)
                .ToList();
        }

        var normalizedQuery = NormalizeText(query!);

        return memories
            .Select(memory => new
            {
                Memory = memory,
                Score = ScoreMemory(memory, normalizedQuery, terms)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Memory.Importance)
            .ThenByDescending(x => x.Memory.LastUsedAt ?? x.Memory.CreatedAt)
            .Take(maxResults)
            .Select(x => x.Memory)
            .ToList();
    }

    private static int ScoreMemory(UserMemory memory, string normalizedQuery, string[] terms)
    {
        var haystack = $"{NormalizeCategory(memory.Category)} {NormalizeText(memory.Content)}";
        var termMatches = terms.Count(term => haystack.Contains(term, StringComparison.Ordinal));
        var exactPhraseBonus = haystack.Contains(normalizedQuery, StringComparison.Ordinal) ? 120 : 0;
        var categoryBonus = terms.Any(term => NormalizeCategory(memory.Category).Contains(term, StringComparison.Ordinal)) ? 20 : 0;
        var importanceBonus = memory.Importance * 10;
        var recencyBonus = CalculateRecencyBonus(memory);

        if (termMatches == 0 && exactPhraseBonus == 0)
        {
            return 0;
        }

        return termMatches * 100 + exactPhraseBonus + categoryBonus + importanceBonus + recencyBonus;
    }

    private static int CalculateRecencyBonus(UserMemory memory)
    {
        var referenceTime = memory.LastUsedAt ?? memory.CreatedAt;
        var ageDays = (DateTime.UtcNow - referenceTime).TotalDays;
        return (int)Math.Max(0, 30 - ageDays);
    }

    private static string[] Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return NormalizeText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= MinimumSearchTermLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumMemorySearchTerms)
            .ToArray();
    }

    private static string NormalizeText(string value)
    {
        var filtered = new string(value
            .ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            .ToArray());

        return string.Join(
            " ",
            filtered
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
