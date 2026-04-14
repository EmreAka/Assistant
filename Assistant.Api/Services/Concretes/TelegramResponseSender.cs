using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace Assistant.Api.Services.Concretes;

public class TelegramResponseSender(
    ITelegramBotClient botClient,
    ILogger<TelegramResponseSender> logger
) : ITelegramResponseSender
{
    private const int TelegramMessageCharacterLimit = 4096;
    private static readonly string[] PreferredSplitSeparators = ["\n\n", "\n", " "];

    public async Task SendResponseAsync(
        long chatId,
        string responseText,
        CancellationToken cancellationToken)
    {
        var chunks = SplitTelegramMessage(responseText).ToList();
        if (chunks.Count == 0)
        {
            chunks.Add("I couldn't generate a response. Please try again.");
        }

        foreach (var chunk in chunks)
        {
            await SendChunkAsync(chatId, chunk, cancellationToken);
        }
    }

    private async Task SendChunkAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (IsMarkdownParseException(ex))
        {
            logger.LogWarning(ex, "Failed to send Telegram message chunk with Markdown. Falling back to plain text.");
            await botClient.SendMessage(
                chatId: chatId,
                text: text,
                cancellationToken: cancellationToken);
        }
    }

    private static IEnumerable<string> SplitTelegramMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var remaining = text.Replace("\r\n", "\n");

        while (remaining.Length > TelegramMessageCharacterLimit)
        {
            var splitIndex = FindSplitIndex(remaining, TelegramMessageCharacterLimit);
            var chunk = remaining[..splitIndex].TrimEnd();

            if (chunk.Length == 0)
            {
                splitIndex = TelegramMessageCharacterLimit;
                chunk = remaining[..splitIndex];
            }

            yield return chunk;
            remaining = remaining[splitIndex..].TrimStart();
        }

        if (!string.IsNullOrWhiteSpace(remaining))
        {
            yield return remaining;
        }
    }

    private static int FindSplitIndex(string text, int maxLength)
    {
        var searchWindow = Math.Min(maxLength, text.Length);
        var minimumAcceptedIndex = searchWindow / 2;

        foreach (var separator in PreferredSplitSeparators)
        {
            var index = text.LastIndexOf(separator, searchWindow - 1, searchWindow, StringComparison.Ordinal);
            if (index >= minimumAcceptedIndex)
            {
                return index + separator.Length;
            }
        }

        return searchWindow;
    }

    private static bool IsMarkdownParseException(ApiRequestException exception)
    {
        return exception.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase);
    }
}
