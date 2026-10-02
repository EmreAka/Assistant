using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Services.Abstracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Features.Chat.Commands;

// Shows the assistant's current (decayed) mood, built in code without a model call (see EMOTION_PLAN.md step 5).
public class MoodCommand(
    ApplicationDbContext dbContext,
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

        var telegramUserId = await dbContext.TelegramUsers
            .AsNoTracking()
            .Where(x => x.ChatId == chatId.Value)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (telegramUserId is null)
        {
            await responseSender.SendResponseAsync(chatId.Value, "Kullanıcı kaydı bulunamadı, önce /start yazabilirsin.", cancellationToken);
            return;
        }

        var state = await emotionService.GetAsync(telegramUserId.Value, cancellationToken);

        var response = new StringBuilder();
        response.AppendLine($"*{GetEmoji(state.Valence, state.Arousal)} Ruh hâli*");
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

    // Same quadrants around the baseline as EmotionService's derived labels.
    private string GetEmoji(double valence, double arousal)
    {
        var valenceOffset = valence - _options.BaselineValence;
        var arousalOffset = arousal - _options.BaselineArousal;

        if (Math.Abs(valenceOffset) < EmotionService.BaselineMoodRange && Math.Abs(arousalOffset) < EmotionService.BaselineMoodRange)
        {
            return "😌";
        }

        return (valenceOffset >= 0, arousalOffset >= 0) switch
        {
            (true, true) => "😄",
            (true, false) => "😊",
            (false, true) => "😬",
            (false, false) => "😔"
        };
    }

    // Mood and reason are model output; legacy Markdown would otherwise read "_" or "*" in them as formatting.
    private static string EscapeMarkdown(string text) => Regex.Replace(text, @"([_*`\[])", @"\$1");
}
