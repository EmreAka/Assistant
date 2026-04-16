using Assistant.Api.Features.UserManagement.Services;
using Assistant.Api.Services.Abstracts;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Features.UserManagement.Commands;

public class MemoryCommand(
    IMemoryService memoryService,
    ITelegramResponseSender responseSender
) : IBotCommand
{
    public string Command => "memory";
    public string Description => "Aktif memory manifest'ini gösterir.";

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

        var manifest = await memoryService.GetActiveManifestRecordAsync(chatId.Value, cancellationToken);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Content))
        {
            await responseSender.SendResponseAsync(
                chatId.Value,
                "Aktif memory manifest bulunamadı.",
                cancellationToken);
            return;
        }

        var response = $$"""
                         *🧠 Aktif Memory*
                         Versiyon: {{manifest.Version}}
                         Güncellendi: {{manifest.UpdatedAt:dd.MM.yyyy HH:mm}} UTC

                         {{manifest.Content}}
                         """;

        await responseSender.SendResponseAsync(chatId.Value, response, cancellationToken);
    }
}
