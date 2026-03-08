using Assistant.Api.Domain.Configurations;
using Assistant.Api.Services.Concretes;
using Hangfire;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class BotController : ControllerBase
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<BotController> _logger;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IOptions<BotOptions> _botOptions;

    public BotController(ITelegramBotClient botClient, ILogger<BotController> logger, IBackgroundJobClient backgroundJobClient, IOptions<BotOptions> botOptions)
        => (_botClient, _logger, _backgroundJobClient, _botOptions) = (botClient, logger, backgroundJobClient, botOptions);


    [HttpPost("update")]
    public async Task<IActionResult> ReceiveUpdate([FromBody] Update update, CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var headerValue) ||
            !IsSecretTokenValid(headerValue, _botOptions.Value.SecretToken))
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

        try
        {
            var jobId = _backgroundJobClient.Enqueue<CommandUpdateJob>(job => job.ExecuteAsync(update));
            _logger.LogInformation("Queued Telegram update for background processing. UpdateId: {UpdateId}, JobId: {JobId}", update.Id, jobId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to enqueue Telegram update. UpdateId: {UpdateId}", update.Id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Ok();
    }

    private static bool IsSecretTokenValid(StringValues headerValues, string configuredSecret)
    {
        if (headerValues.Count != 1)
            return false;

        var headerSecret = headerValues[0];
        if (string.IsNullOrWhiteSpace(headerSecret) || string.IsNullOrWhiteSpace(configuredSecret))
            return false;

        var headerBytes = Encoding.UTF8.GetBytes(headerSecret);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredSecret);
        var headerHash = SHA256.HashData(headerBytes);
        var configuredHash = SHA256.HashData(configuredBytes);

        return CryptographicOperations.FixedTimeEquals(headerHash, configuredHash);
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
