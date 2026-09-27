using System.Text;
using Assistant.Api.Features.UserManagement.Models;
using Assistant.Api.Features.UserManagement.Services;
using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Features.UserManagement.Commands;

public class MemoryCommand(
    IMemoryItemService memoryItemService,
    ITelegramResponseSender responseSender
) : IBotCommand
{
    // Turkish headings; also keeps "_" in category keys out of Telegram Markdown.
    private static readonly Dictionary<string, string> CategoryLabels = new(StringComparer.Ordinal)
    {
        [UserMemoryItemCategories.Identity] = "Kimlik",
        [UserMemoryItemCategories.Preference] = "Tercihler",
        [UserMemoryItemCategories.Relationship] = "İlişkiler",
        [UserMemoryItemCategories.WorkEducation] = "İş / Eğitim",
        [UserMemoryItemCategories.Health] = "Sağlık",
        [UserMemoryItemCategories.Goal] = "Hedefler",
        [UserMemoryItemCategories.Routine] = "Rutinler",
        [UserMemoryItemCategories.Interest] = "İlgi Alanları",
        [UserMemoryItemCategories.Other] = "Diğer"
    };

    public string Command => "memory";
    public string Description => "Aktif memory kayıtlarını gösterir.";

    public async Task ExecuteAsync(
        Update update,
        ITelegramBotClient client,
        CancellationToken cancellationToken
    )
    {
        var chatId = update.Message?.Chat.Id;
        if (chatId is null)
        {
            return;
        }

        var items = await memoryItemService.GetActiveItemsAsync(chatId.Value, cancellationToken);
        if (items.Count == 0)
        {
            await responseSender.SendResponseAsync(
                chatId.Value,
                "Aktif memory kaydı bulunamadı.",
                cancellationToken);
            return;
        }

        var coreItems = items.Where(x => x.IsCore).ToList();
        var response = new StringBuilder();
        response.AppendLine("*🧠 Aktif Memory*");
        response.AppendLine($"Toplam: {items.Count} kayıt ({coreItems.Count} temel)");
        response.AppendLine($"Son güncelleme: {items.Max(x => x.UpdatedAt):dd.MM.yyyy HH:mm} UTC");

        if (coreItems.Count > 0)
        {
            AppendSection(response, "Temel", coreItems);
        }

        foreach (var group in items.Where(x => !x.IsCore).GroupBy(x => x.Category))
        {
            AppendSection(response, CategoryLabels.GetValueOrDefault(group.Key, group.Key), group);
        }

        await responseSender.SendResponseAsync(chatId.Value, response.ToString().TrimEnd(), cancellationToken);
    }

    // Item IDs are shown so a single item can be referenced later (e.g. a future /forget <id>).
    private static void AppendSection(StringBuilder response, string heading, IEnumerable<MemoryItemSummary> items)
    {
        response.AppendLine();
        response.AppendLine($"*{heading}*");
        foreach (var item in items)
        {
            response.AppendLine($"#{item.Id} {item.Text}");
        }
    }
}
