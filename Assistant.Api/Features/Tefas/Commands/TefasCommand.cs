using Assistant.Api.Features.Tefas.Services;
using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Features.Tefas.Commands;

public class TefasCommand(
    ITefasAnalysisService tefasAnalysisService,
    ITelegramResponseSender responseSender,
    ILogger<TefasCommand> logger
) : IBotCommand
{
    public string Command => "tefas";
    public string Description => "TEFAS fon analizi yapar.";

    public async Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message?.Chat is null || string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        var fundCode = ExtractFundCode(message.Text);
        if (string.IsNullOrWhiteSpace(fundCode))
        {
            await responseSender.SendResponseAsync(
                message.Chat.Id,
                "Kullanim: /tefas AFT",
                cancellationToken);
            return;
        }

        try
        {
            var response = await tefasAnalysisService.AnalyzeAsync(
                message.Chat.Id,
                fundCode,
                cancellationToken);

            await responseSender.SendResponseAsync(
                message.Chat.Id,
                response.UserMessage,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TEFAS command failed for input: {Input}", message.Text);
            await responseSender.SendResponseAsync(
                message.Chat.Id,
                "TEFAS verisini cekerken bir hata olustu. Lutfen tekrar dene.",
                cancellationToken);
        }
    }

    private static string ExtractFundCode(string messageText)
    {
        var trimmed = messageText.Trim();
        if (!trimmed.StartsWith('/'))
        {
            return string.Empty;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        return parts[1]
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0]
            .ToUpperInvariant();
    }
}
