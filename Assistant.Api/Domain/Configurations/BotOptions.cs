namespace Assistant.Api.Domain.Configurations;

public class BotOptions
{
    public string BotToken { get; set; } = string.Empty;
    public string WebhookUrl { get; set; } = string.Empty;
    public string SecretToken { get; set; } = string.Empty;
    public List<long> AllowedChatIds { get; set; } = [];
}
