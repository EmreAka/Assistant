using System.Globalization;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public interface ITtsDirectorService
{
    /// <summary>
    /// Returns the text with xAI speech tags added, or null when direction is off, fails, or the
    /// model changed the words. Null means: synthesize the plain text.
    /// </summary>
    Task<string?> DirectAsync(string text, AgentEmotionState? mood, CancellationToken cancellationToken);
}

public class TtsDirectorService(
    IChatClient chatClient,
    IOptions<AiProvidersOptions> aiOptions,
    ILogger<TtsDirectorService> logger
) : ITtsDirectorService
{
    private readonly string _model = aiOptions.Value.XAI.TtsDirectorModel;
    private readonly ReasoningEffort _reasoningEffort = aiOptions.Value.OpenRouter.Reasoning.TtsDirection;

    // chatClient is the shared singleton from BotServiceRegistration; ModelId switches the model per
    // request, like MemoryExtractionAgentService. No web search factory.
    public async Task<string?> DirectAsync(string text, AgentEmotionState? mood, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_model))
        {
            return null;
        }

        try
        {
            var response = await chatClient.GetResponseAsync<TtsScript>(
                new ChatMessage(ChatRole.User, BuildInput(text, mood)),
                new ChatOptions
                {
                    Instructions = BuildInstructions(),
                    ModelId = _model,
                    Temperature = 0.7f,
                    Reasoning = new ReasoningOptions { Effort = _reasoningEffort }
                },
                cancellationToken: cancellationToken);

            var script = response.Result.Text?.Trim();
            if (string.IsNullOrEmpty(script) || !TtsSpeechTags.IsValidScript(text, script))
            {
                logger.LogWarning("TTS script rejected; using the plain text. Script: {Script}", script);
                return null;
            }

            logger.LogInformation("TTS script: {Script}", script);
            return script;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TTS direction failed; using the plain text.");
            return null;
        }
    }

    private static string BuildInstructions()
    {
        return $"""
                You are a voice director. You receive a message a persona wrote in a Telegram chat,
                and the persona's current mood. Add xAI text-to-speech tags so the message sounds
                like the persona saying it aloud in that mood.

                Inline tags mark a sound at one point: {string.Join(" ", TtsSpeechTags.Inline.Select(x => $"[{x}]"))}
                Wrapping tags change how a phrase is said: {string.Join(" ", TtsSpeechTags.Wrapping.Select(x => $"<{x}>...</{x}>"))}

                Rules:
                - Never change, add, remove or reorder words or punctuation. Only insert tags.
                - Use tags where a person would naturally make that sound or change their voice; a
                  short message may need one tag or none. Don't stack tags.
                - Place inline tags next to punctuation ("Really? [laugh] That's great!").
                - Wrap complete phrases, not single words. Close every wrapping tag; nest at most two
                  ("<slow><soft>Goodnight.</soft></slow>").
                - Follow the mood: a cheerful mood can laugh or chuckle at something funny; a sad or
                  tired one can sigh, slow down or go soft; a tense one can breathe or build intensity.
                  The content of the message matters more than the mood: never laugh at bad news.
                - The message may be in any language; tag names always stay in English as listed.
                - Return the full tagged message in text.
                """;
    }

    private static string BuildInput(string text, AgentEmotionState? mood)
    {
        var moodElement = mood is null
            ? "<mood>neutral</mood>"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"""<mood valence="{mood.Valence:0.00}" arousal="{mood.Arousal:0.00}">{mood.Mood}{(string.IsNullOrWhiteSpace(mood.Reason) ? "" : $" ({mood.Reason})")}</mood>""");

        return $"""
                {moodElement}
                <message>
                {text}
                </message>
                """;
    }

    private sealed record TtsScript(string Text);
}
