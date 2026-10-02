using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Services.Abstracts;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Features.Chat.Commands;

// Shows the assistant's current (decayed) mood, built in code without a model call (see EMOTION_PLAN.md step 5).
public class MoodCommand(
    IEmotionService emotionService,
    IAssistantTimeService assistantTimeService,
    IOptions<EmotionOptions> emotionOptions,
    ITelegramResponseSender responseSender
) : IBotCommand
{
    private readonly EmotionOptions _options = emotionOptions.Value;

    public string Command => "mood";
    public string Description => "Asistanın şu anki ruh hâlini gösterir.";

    public async Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken)
    {
        var chatId = update.Message?.Chat.Id;
        if (chatId is null)
        {
            return;
        }

        if (!_options.Enabled)
        {
            await responseSender.SendResponseAsync(chatId.Value, "Ruh hâli özelliği kapalı.", cancellationToken);
            return;
        }

        var state = await emotionService.GetByChatIdAsync(chatId.Value, cancellationToken);
        if (state is null)
        {
            await responseSender.SendResponseAsync(chatId.Value, "Kullanıcı kaydı bulunamadı, önce /start yazabilirsin.", cancellationToken);
            return;
        }

        var response = new StringBuilder();
        response.AppendLine($"*{GetEmoji(state)} Ruh hâli*");
        response.AppendLine($"Şu an: {EscapeMarkdown(state.Mood)}");
        if (!string.IsNullOrWhiteSpace(state.Reason))
        {
            response.AppendLine($"Neden: {EscapeMarkdown(state.Reason)}");
        }

        response.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Mutluluk: {state.Valence:0.00} · Enerji: {state.Arousal:0.00}"));

        // LastTurnId is null for the baseline mood, which has never been updated.
        if (state.LastTurnId is not null)
        {
            var updatedAtLocal = assistantTimeService.FormatUtcForDisplay(
                state.UpdatedAt,
                assistantTimeService.DefaultTimeZoneId,
                "dd.MM.yyyy HH:mm");
            response.AppendLine($"Son değişim: {updatedAtLocal}");
        }

        await responseSender.SendResponseAsync(chatId.Value, response.ToString().TrimEnd(), cancellationToken);
    }

    private string GetEmoji(AgentEmotionState state) => EmotionService.GetQuadrant(state.Valence, state.Arousal, _options) switch
    {
        MoodQuadrant.Cheerful => "😄",
        MoodQuadrant.Content => "😊",
        MoodQuadrant.Tense => "😬",
        MoodQuadrant.Down => "😔",
        _ => "😌"
    };

    // Mood and reason are model output; legacy Markdown would otherwise read "_" or "*" in them as formatting.
    private static string EscapeMarkdown(string text) => Regex.Replace(text, @"([_*`\[])", @"\$1");
}
