namespace Assistant.Api.Features.Chat.Services;

public interface IChatTurnService
{
    Task<ChatTurnSaveResult?> SaveTurnAsync(long chatId, string userMessage, string assistantMessage, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatTurnSearchResult>> SearchTurnsAsync(long chatId, string query, int maxResults, CancellationToken cancellationToken);
    Task<string?> GetLastAssistantMessageAsync(long chatId, CancellationToken cancellationToken);
}

public sealed record ChatTurnSaveResult(
    int TurnId,
    int TelegramUserId,
    long ChatId,
    DateTime CreatedAtUtc);

// Ranks are 1-based; null means the turn wasn't in that search's results.
public sealed record FusedSearchResult(
    int Id,
    double Score,
    int? FullTextRank,
    int? SemanticRank)
{
    public string Source => (FullTextRank, SemanticRank) switch
    {
        (not null, not null) => "both",
        (not null, null) => "fulltext",
        _ => "semantic"
    };
}

public sealed record ChatTurnSearchResult(
    int Id,
    string UserMessage,
    string AssistantMessage,
    DateTime CreatedAt,
    double Score);
