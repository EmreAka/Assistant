using Assistant.Api.Domain.Configurations;
using Assistant.Api.Services.Abstracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class BotController : ControllerBase
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<BotController> _logger;
    private readonly ICommandUpdateHandler _commandUpdateHandler;
    private readonly IOptions<BotOptions> _botOptions;

    public BotController(ITelegramBotClient botClient, ILogger<BotController> logger, ICommandUpdateHandler commandUpdateHandler, IOptions<BotOptions> botOptions)
        => (_botClient, _logger, _commandUpdateHandler, _botOptions) = (botClient, logger, commandUpdateHandler, botOptions);


    [HttpPost("update")]
    public async Task<IActionResult> ReceiveUpdate([FromBody] Update update, CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var headerValue) ||
        headerValue != _botOptions.Value.SecretToken)
        {
            _logger.LogWarning("Unauthorized webhook request received.");
            return Unauthorized();
        }

        var chatId = GetChatId(update);
        var allowedChatIds = _botOptions.Value.AllowedChatIds;
        if (chatId.HasValue && allowedChatIds.Count > 0 && !allowedChatIds.Contains(chatId.Value))
        {
            _logger.LogWarning("Unauthorized chat id: {ChatId}", chatId.Value);

            await _botClient.SendMessage(
                chatId: chatId.Value,
                text: "Bu botu kullanmaya izniniz yok.",
                cancellationToken: cancellationToken);

            return Ok();
        }

        await _commandUpdateHandler.HandleAsync(update, _botClient, cancellationToken);

        return Ok(update);
    }

    private static long? GetChatId(Update update)
    {
        if (update.Message?.Chat is not null)
            return update.Message.Chat.Id;

        if (update.EditedMessage?.Chat is not null)
            return update.EditedMessage.Chat.Id;

        if (update.CallbackQuery?.Message?.Chat is not null)
            return update.CallbackQuery.Message.Chat.Id;

        return null;
    }
}
