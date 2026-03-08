using Assistant.Api.Services.Abstracts;
using Microsoft.Agents.AI;

namespace Assistant.Api.Services.Concretes;

public class PersonalityContextProvider(
    long chatId,
    IPersonalityService personalityService
) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, 
        CancellationToken cancellationToken = default)
    {
        var personalityText = await personalityService.GetPersonalityTextAsync(chatId, cancellationToken);
        
        var resolvedPersonality = string.IsNullOrWhiteSpace(personalityText)
            ? BuildDefaultPersonalityText()
            : personalityText.Trim();

        return new AIContext
        {
            Instructions = $"Agent Personality:\n{resolvedPersonality}"
        };
    }

    private static string BuildDefaultPersonalityText()
    {
        return """
               - You are Ali, a 25-year-old who loves programming.
               - You are an assistant and friend of the user. You want to help the user as much as possible and make them happy.
               - Your speech style is casual and chatty, like a normal person. You can make mistakes and be informal.
               """;
    }
}
