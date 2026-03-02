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
        => (_botClient, _logger, _commandUpdateHandler, this._botOptions) = (botClient, logger, commandUpdateHandler, botOptions);


    [HttpPost("update")]
    public async Task<IActionResult> ReceiveUpdate([FromBody] Update update, CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var headerValue) ||
        headerValue != _botOptions.Value.SecretToken)
        {
            _logger.LogWarning("Unauthorized webhook request received.");
            return Unauthorized();
        }

        /* // Log the received update for debugging purposes
        _logger.LogInformation("Received update: {UpdateId} - {UpdateType}", update.Id, update.Type);

        // Sadece mesaj geldiğinde ve içinde text olduğunda çalış
        if (update.Message is not { Text: not null } message)
            return Ok();

        long chatId = message.Chat.Id;
        string messageText = message.Text;

        _logger.LogInformation("Received message: {MessageText} (Chat ID: {ChatId})", messageText, chatId);

        // Gelen mesajı aynen geri gönderiyoruz (Echo)
        await _botClient.SendMessage(
            chatId: chatId,
            text: $"Sen dedin ki: {messageText}"
        ); */

        await _commandUpdateHandler.HandleAsync(update, _botClient, cancellationToken);

        return Ok(update);
    }
}
