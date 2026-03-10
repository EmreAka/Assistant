using Assistant.Api.Domain.Configurations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Telegram.Bot.Types;

namespace Assistant.Api.Services.Abstracts;

public interface IAgentService
{
    Task<string> RunAsync(
        long chatId,
        string userInput,
        string? systemInstructionsAugmentation = null,
        IEnumerable<AITool>? additionalTools = null,
        CancellationToken cancellationToken = default);
}
