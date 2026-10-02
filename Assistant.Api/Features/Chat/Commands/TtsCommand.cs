using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Services.Abstracts;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Features.Chat.Commands;

public class TtsCommand(
    IChatTurnService chatTurnService,
    ITextToSpeechService textToSpeechService,
    IEmotionService emotionService,
    IOptions<EmotionOptions> emotionOptions,
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
            var mood = await GetMoodAsync(chatId.Value, cancellationToken);
            var audio = await textToSpeechService.SynthesizeAsync(lastAssistantMessage, mood, cancellationToken);

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

    // The current mood shifts the voice slightly. Without it the audio is still sent, just neutral.
    private async Task<AgentEmotionState?> GetMoodAsync(long chatId, CancellationToken cancellationToken)
    {
        if (!emotionOptions.Value.Enabled)
        {
            return null;
        }

        try
        {
            return await emotionService.GetByChatIdAsync(chatId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Mood for TTS could not be loaded; continuing without it. ChatId: {ChatId}", chatId);
            return null;
        }
    }
}
