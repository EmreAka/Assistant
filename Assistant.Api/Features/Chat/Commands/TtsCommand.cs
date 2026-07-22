using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Features.Chat.Commands;

public class TtsCommand(
    IChatTurnService chatTurnService,
    ITextToSpeechService textToSpeechService,
    ITelegramResponseSender responseSender,
    ILogger<TtsCommand> logger
) : IBotCommand
{
    public string Command => "tts";
    public string Description => "Asistanın son mesajını sesli olarak gönderir.";

    public async Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken)
    {
        var chatId = update.Message?.Chat.Id;
        if (chatId is null) return;

        var lastAssistantMessage = await chatTurnService.GetLastAssistantMessageAsync(chatId.Value, cancellationToken);
        if (string.IsNullOrWhiteSpace(lastAssistantMessage))
        {
            await responseSender.SendResponseAsync(
                chatId.Value,
                "Seslendirilecek bir asistan mesajı bulunamadı.",
                cancellationToken);
            return;
        }

        try
        {
            var audio = await textToSpeechService.SynthesizeAsync(lastAssistantMessage, cancellationToken);

            await using var audioStream = new MemoryStream(audio);
            await client.SendAudio(
                chatId: chatId.Value,
                audio: InputFile.FromStream(audioStream, "response.mp3"),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TTS command execution failed. ChatId: {ChatId}", chatId);
            await responseSender.SendResponseAsync(
                chatId.Value,
                "Ses oluşturulurken bir hata oluştu, lütfen tekrar dener misin?",
                cancellationToken);
        }
    }
}
