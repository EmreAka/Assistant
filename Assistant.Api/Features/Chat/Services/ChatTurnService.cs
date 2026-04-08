using Assistant.Api.Data;
using Assistant.Api.Features.Chat.Models;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Api.Features.Chat.Services;

public class ChatTurnService(
    ApplicationDbContext dbContext,
    ILogger<ChatTurnService> logger
) : IChatTurnService
{
    private const int MinimumSearchTermLength = 2;
    private const int MaximumSearchTerms = 8;

    public async Task SaveTurnAsync(
        long chatId,
        string userMessage,
        string assistantMessage,
        CancellationToken cancellationToken)
    {
        var normalizedUserMessage = NormalizeStoredText(userMessage);
        var normalizedAssistantMessage = NormalizeStoredText(assistantMessage);

        if (string.IsNullOrWhiteSpace(normalizedUserMessage) || string.IsNullOrWhiteSpace(normalizedAssistantMessage))
        {
            return;
        }

        var userId = await dbContext.TelegramUsers
            .AsNoTracking()
            .Where(x => x.ChatId == chatId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId == null)
        {
            logger.LogWarning("Chat turn could not be saved because telegram user was not found. ChatId: {ChatId}", chatId);
            return;
        }

        dbContext.ChatTurns.Add(new ChatTurn
        {
            TelegramUserId = userId.Value,
            UserMessage = normalizedUserMessage,
            AssistantMessage = normalizedAssistantMessage,
            CreatedAt = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatTurnSearchResult>> SearchTurnsAsync(
        long chatId,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (maxResults <= 0)
        {
            return [];
        }

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
            .Take(maxResults)
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
