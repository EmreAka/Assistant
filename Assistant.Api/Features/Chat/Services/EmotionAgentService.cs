using System.Globalization;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class EmotionAgentService(
    IChatClient chatClient,
    IOptions<AiProvidersOptions> aiOptions,
    IOptions<EmotionOptions> emotionOptions
) : IEmotionAgentService
{
    // Meanings of the default event types (EMOTION_PLAN.md step 3). An event type added only in
    // config is listed by name.
    private static readonly Dictionary<string, string> EventMeanings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["neutral"] = "plain questions, tasks, reminders, small talk",
        ["affection"] = "the user is warm, thankful, or compliments you",
        ["playful"] = "jokes, banter, teasing in good spirit",
        ["good_news"] = "the user shares something good in their life",
        ["bad_news"] = "the user shares something bad, or is stressed or sad",
        ["cold"] = "short, dismissive, ignores what you said",
        ["rude"] = "insults or hostility toward you"
    };

    private readonly ReasoningEffort _reasoningEffort = aiOptions.Value.OpenRouter.Reasoning.Emotion;
    private readonly EmotionOptions _options = emotionOptions.Value;

    // chatClient is the shared singleton from BotServiceRegistration; ModelId switches the model per
    // request, like MemoryExtractionAgentService. No web search factory.
    public async Task<EmotionReaction> ReactAsync(
        string personality,
        AgentEmotionState currentMood,
        IReadOnlyList<string> userMemory,
        string userMessage,
        string assistantMessage,
        CancellationToken cancellationToken)
    {
        var response = await chatClient.GetResponseAsync<EmotionReaction>(
            new ChatMessage(ChatRole.User, BuildInput(currentMood, userMemory, userMessage, assistantMessage)),
            new ChatOptions
            {
                Instructions = BuildInstructions(personality),
                ModelId = string.IsNullOrWhiteSpace(_options.Model) ? null : _options.Model,
                Temperature = 0.2f,
                Reasoning = new ReasoningOptions { Effort = _reasoningEffort }
            },
            cancellationToken: cancellationToken);

        // Throws when the model output isn't valid JSON for EmotionReaction; the job logs and drops it.
        return response.Result;
    }

    private string BuildInstructions(string personality)
    {
        var eventTypes = string.Join(
            Environment.NewLine,
            _options.Events.Keys.Select(key => EventMeanings.TryGetValue(key, out var meaning)
                ? $"- \"{key}\": {meaning}"
                : $"- \"{key}\""));

        return $"""
                You decide how a persona's mood reacts to one turn of a Telegram chat with the user.

                <persona>
                {personality.Trim()}
                </persona>

                You receive the persona's current mood, what the persona remembers about the user,
                and the latest turn: the user's message and the persona's reply. Classify the turn
                from the persona's point of view, mostly by what the user said and how they said it.
                The persona's own reply is context only.

                <user_memory> lists what the persona knows about the user. It explains why something
                in the turn matters to them: news about someone or something they care about can be a
                higher intensity. It is never a reason to change the mood on its own; classify the
                turn, not the memory.

                eventType is exactly one of:
                {eventTypes}

                intensity is exactly one of: {string.Join(", ", _options.IntensityMultipliers.Keys.Select(x => $"\"{x}\""))}.

                Rules:
                - Pick the single main thing that happened in the turn.
                - When in doubt, pick "{EmotionReaction.FallbackEventType}" and "{EmotionReaction.FallbackIntensity}".
                - mood is a short label for how the persona feels after this turn, at most
                  {AgentEmotionState.MaxMoodLength} characters, in English (e.g. "cheerful",
                  "a bit worried about you"). Empty keeps the current label.
                - reason is one short sentence on why, at most {AgentEmotionState.MaxReasonLength}
                  characters, in English (e.g. "user's exam is tomorrow"). Empty keeps the current reason;
                  use empty for neutral turns.
                """;
    }

    private static string BuildInput(
        AgentEmotionState currentMood,
        IReadOnlyList<string> userMemory,
        string userMessage,
        string assistantMessage)
    {
        var reason = string.IsNullOrWhiteSpace(currentMood.Reason) ? "none" : currentMood.Reason;
        var memory = userMemory.Count == 0
            ? "none"
            : string.Join(Environment.NewLine, userMemory.Select(x => $"- {x}"));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             <current_mood label="{currentMood.Mood}" valence="{currentMood.Valence:0.00}" arousal="{currentMood.Arousal:0.00}">{reason}</current_mood>
             <user_memory>
             {memory}
             </user_memory>
             <turn>
             <user>{userMessage}</user>
             <assistant>{assistantMessage}</assistant>
             </turn>
             """);
    }
}
