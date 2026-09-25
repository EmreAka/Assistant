using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

    // Semantic search: nearest turns by meaning. Distance is the cosine distance (lower = closer).
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

        try
        {
            var queryVector = await embeddingService.EmbedQueryAsync(query, cancellationToken);

            // Vector search always returns the "closest" turns, even when nothing is related,
            // so hits beyond MaxCosineDistance are dropped.
            var results = await dbContext.ChatTurns
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
                .Take(maxResults)
                .Select(x => new ChatTurnSearchResult(x.Id, x.UserMessage, x.AssistantMessage, x.CreatedAt, x.Distance))
                .ToListAsync(cancellationToken);

            LogSearchResults(chatId, query, results);
            return results;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Search must never break a chat reply; continue without past chat turns.
            logger.LogWarning(ex, "Chat turn search failed; continuing without past chat turns. ChatId: {ChatId}", chatId);
            return [];
        }
    }

    // Temporary tuning aid for picking MaxCosineDistance. Enable with a Debug log level for this class.
    private void LogSearchResults(
        long chatId,
        string query,
        IReadOnlyList<ChatTurnSearchResult> results)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        logger.LogDebug(
            "Chat turn search. ChatId: {ChatId}, Query: {Query}, ResultCount: {ResultCount}",
            chatId,
            query,
            results.Count);

        foreach (var result in results)
        {
            logger.LogDebug(
                "Chat turn search hit. ChatTurnId: {ChatTurnId}, CosineDistance: {CosineDistance}",
                result.Id,
                result.Distance.ToString("F3"));
        }
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
}
