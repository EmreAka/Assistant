namespace Assistant.Api.Services.Abstracts;

public interface ITelegramResponseSender
{
    Task SendResponseAsync(
        long chatId,
        string responseText,
        CancellationToken cancellationToken);
}
