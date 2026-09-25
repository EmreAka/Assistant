using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Assistant.Api.Features.Chat.Services;

public class ChatTurnService(
    ApplicationDbContext dbContext,
    IChatTurnEmbeddingService embeddingService,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<ChatTurnService> logger
) : IChatTurnService
{
    private readonly EmbeddingOptions _embeddingOptions = embeddingOptions.Value;

    private const int MinimumSearchTermLength = 2;
    private const int MaximumSearchTerms = 8;
    // Standard Reciprocal Rank Fusion constant; dampens the gap between top ranks.
    private const int ReciprocalRankK = 60;

    public async Task<ChatTurnSaveResult?> SaveTurnAsync(
        long chatId,
        string userMessage,
        string assistantMessage,
        CancellationToken cancellationToken)
    {
        var normalizedUserMessage = NormalizeStoredText(userMessage);
        var normalizedAssistantMessage = NormalizeStoredText(assistantMessage);

        if (string.IsNullOrWhiteSpace(normalizedUserMessage) || string.IsNullOrWhiteSpace(normalizedAssistantMessage))
        {
            return null;
        }

        var userId = await dbContext.TelegramUsers
            .AsNoTracking()
            .Where(x => x.ChatId == chatId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId == null)
        {
            logger.LogWarning("Chat turn could not be saved because telegram user was not found. ChatId: {ChatId}", chatId);
            return null;
        }

        var createdAtUtc = DateTime.UtcNow;
        var turn = new ChatTurn
        {
            TelegramUserId = userId.Value,
            UserMessage = normalizedUserMessage,
            AssistantMessage = normalizedAssistantMessage,
            CreatedAt = createdAtUtc
        };

        dbContext.ChatTurns.Add(turn);

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ChatTurnSaveResult(turn.Id, userId.Value, chatId, createdAtUtc);
    }

    // Hybrid search: full-text and semantic results are merged with Reciprocal Rank Fusion.
    public async Task<IReadOnlyList<ChatTurnSearchResult>> SearchTurnsAsync(
        long chatId,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (maxResults <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var fullTextResults = await SearchTurnsFullTextAsync(chatId, query, cancellationToken);

        IReadOnlyList<ChatTurnSearchResult> semanticResults = [];
        try
        {
            var queryVector = await embeddingService.EmbedQueryAsync(query, cancellationToken);
            semanticResults = await SearchTurnsSemanticAsync(chatId, queryVector, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Search must never break a chat reply; fall back to full-text results only.
            logger.LogWarning(ex, "Semantic chat turn search failed; using full-text results only. ChatId: {ChatId}", chatId);
        }

        var turnsById = fullTextResults
            .Concat(semanticResults)
            .DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id);

        return [.. FuseByReciprocalRank(
                fullTextResults.Select(x => x.Id).ToList(),
                semanticResults.Select(x => x.Id).ToList())
            .Take(maxResults)
            .Select(fused => turnsById[fused.Id] with { Score = fused.Score })];
    }

    private async Task<IReadOnlyList<ChatTurnSearchResult>> SearchTurnsFullTextAsync(
        long chatId,
        string query,
        CancellationToken cancellationToken)
    {
        var tsQuery = BuildTsQuery(query);
        if (string.IsNullOrWhiteSpace(tsQuery))
        {
            return [];
        }

        var turns = await dbContext.ChatTurns
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId)
            .Where(x => x.SearchVector.Matches(EF.Functions.ToTsQuery("simple", tsQuery)))
            .Select(x => new
            {
                x.Id,
                x.UserMessage,
                x.AssistantMessage,
                x.CreatedAt,
                Score = x.SearchVector.RankCoverDensity(EF.Functions.ToTsQuery("simple", tsQuery))
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.CreatedAt)
            .Take(_embeddingOptions.SearchCandidates)
            .ToListAsync(cancellationToken);

        if (turns.Count == 0)
        {
            return [];
        }

        return turns
            .Select(turn => new ChatTurnSearchResult(
                turn.Id,
                turn.UserMessage,
                turn.AssistantMessage,
                turn.CreatedAt,
                turn.Score))
            .ToList();
    }

    // Nearest turns by meaning. Score holds the cosine distance here (lower = closer), not a relevance score.
    private async Task<IReadOnlyList<ChatTurnSearchResult>> SearchTurnsSemanticAsync(
        long chatId,
        Vector queryVector,
        CancellationToken cancellationToken)
    {
        // Vector search always returns the "closest" turns, even when nothing is related,
        // so hits beyond MaxCosineDistance are dropped.
        return await dbContext.ChatTurns
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId && x.Embedding != null)
            .Select(x => new
            {
                x.Id,
                x.UserMessage,
                x.AssistantMessage,
                x.CreatedAt,
                Distance = x.Embedding!.CosineDistance(queryVector)
            })
            .Where(x => x.Distance <= _embeddingOptions.MaxCosineDistance)
            .OrderBy(x => x.Distance)
            .Take(_embeddingOptions.SearchCandidates)
            .Select(x => new ChatTurnSearchResult(x.Id, x.UserMessage, x.AssistantMessage, x.CreatedAt, x.Distance))
            .ToListAsync(cancellationToken);
    }

    // Merges ranked ID lists by position only, since ts_rank_cd scores and cosine distances aren't
    // comparable. A turn found by both searches outranks one found by a single search.
    public static IReadOnlyList<(int Id, double Score)> FuseByReciprocalRank(
        IReadOnlyList<int> fullTextIds,
        IReadOnlyList<int> semanticIds)
    {
        var scores = new Dictionary<int, double>();

        foreach (var rankedIds in new[] { fullTextIds, semanticIds })
        {
            for (var index = 0; index < rankedIds.Count; index++)
            {
                var rank = index + 1;
                scores[rankedIds[index]] = scores.GetValueOrDefault(rankedIds[index]) + 1.0 / (ReciprocalRankK + rank);
            }
        }

        // Ties go to the newer turn (higher ID), matching the full-text ordering.
        return scores
            .OrderByDescending(x => x.Value)
            .ThenByDescending(x => x.Key)
            .Select(x => (x.Key, x.Value))
            .ToList();
    }

    public async Task<string?> GetLastAssistantMessageAsync(long chatId, CancellationToken cancellationToken)
    {
        return await dbContext.ChatTurns
            .AsNoTracking()
            .Where(x => x.TelegramUser.ChatId == chatId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.AssistantMessage)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string NormalizeStoredText(string value)
    {
        return string.Join(
            " ",
            value
                .Trim()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? BuildTsQuery(string value)
    {
        var terms = NormalizeStoredText(value)
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSearchTerm)
            .OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumSearchTerms)
            .ToArray();

        if (terms.Length == 0)
        {
            return null;
        }

        return string.Join(
            " | ",
            terms.Select(term => term.Length >= 4 ? $"{term}:*" : term));
    }

    private static string? NormalizeSearchTerm(string value)
    {
        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        return normalized.Length >= MinimumSearchTermLength
            ? normalized
            : null;
    }
}
