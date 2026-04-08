namespace Assistant.Api.Features.Chat.Services;

public interface IChatTurnService
{
    Task SaveTurnAsync(long chatId, string userMessage, string assistantMessage, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatTurnSearchResult>> SearchTurnsAsync(long chatId, string query, int maxResults, CancellationToken cancellationToken);
}

public sealed record ChatTurnSearchResult(
    int Id,
    string UserMessage,
    string AssistantMessage,
    DateTime CreatedAt,
    double Score);
