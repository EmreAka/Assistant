using System.Globalization;
using System.Text;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.UserManagement.Services;

public class MemoryExtractionAgentService(
    IChatClient chatClient,
    IAssistantTimeService assistantTimeService,
    IOptions<AiProvidersOptions> aiOptions,
    IOptions<MemoryItemOptions> memoryItemOptions
) : IMemoryExtractionAgentService
{
    private readonly ReasoningEffort _reasoningEffort = aiOptions.Value.OpenRouter.Reasoning.MemoryConsolidation;
    private readonly MemoryItemOptions _options = memoryItemOptions.Value;

    public async Task<IReadOnlyList<CandidateFact>> ExtractFromTurnsAsync(
        IReadOnlyList<MemoryExtractionTurn> turns,
        CancellationToken cancellationToken)
    {
        if (turns.Count == 0)
        {
            return [];
        }

        var result = await GetResultAsync<CandidateFactList>(
            BuildTurnExtractionInstructions(),
            BuildTurnExtractionInput(turns),
            cancellationToken);

        return result.Facts;
    }

    public async Task<IReadOnlyList<CandidateFact>> ExtractFromManifestAsync(
        string manifest,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return [];
        }

        var result = await GetResultAsync<CandidateFactList>(
            BuildManifestExtractionInstructions(),
            $"""
             <manifest>
             {manifest.Trim()}
             </manifest>
             """,
            cancellationToken);

        return result.Facts;
    }

    public async Task<IReadOnlyList<MemoryDecision>> ReconcileAsync(
        IReadOnlyList<ReconcileCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var result = await GetResultAsync<MemoryDecisionList>(
            BuildReconcileInstructions(),
            BuildReconcileInput(candidates),
            cancellationToken);

        return result.Decisions;
    }

    // chatClient is the shared singleton from BotServiceRegistration. Same settings as
    // MemoryConsolidationAgentService; no web search factory, since memory never needs it.
    private async Task<T> GetResultAsync<T>(string instructions, string input, CancellationToken cancellationToken)
    {
        var response = await chatClient.GetResponseAsync<T>(
            new ChatMessage(ChatRole.User, input),
            new ChatOptions
            {
                Instructions = instructions,
                Temperature = 0.2f,
                Reasoning = new ReasoningOptions { Effort = _reasoningEffort }
            },
            cancellationToken: cancellationToken);

        // Throws when the model output isn't valid JSON for T; the job treats that as a failed run.
        return response.Result;
    }

    private string BuildTurnExtractionInstructions()
    {
        return $"""
                You extract long-term memory facts about the user of a personal Telegram assistant.
                You receive chat turns between the user and the assistant, each inside a <turn> element
                with its id and local time.

                {BuildFactRules()}

                Source rules:
                - Only extract facts the user stated or confirmed in <user> text.
                - <assistant> text is context for understanding the user. It is never a source of facts,
                  even when it contains web search results, suggestions, or claims about the user.
                - Turn relative dates into absolute dates using the turn's local time
                  ("tomorrow" in a turn at 2026-09-27 becomes 2026-09-28).
                - sourceTurnIds lists the ids of the turns each fact comes from.

                Skip:
                - Task and reminder state: pending, scheduled, recurring, completed, cancelled or failed
                  tasks, task IDs, job IDs, cron expressions, due dates, reminder confirmations and
                  execution results. If a scheduling turn shows a lasting preference or routine, keep
                  only the preference.
                - Small talk, one-off questions, and short-lived moods, unless they point to something lasting.
                - Anything the turns don't clearly support.

                Returning an empty list is fine when nothing is worth remembering.
                """;
    }

    private string BuildManifestExtractionInstructions()
    {
        return $"""
                You convert an existing user memory manifest of a personal Telegram assistant into
                separate long-term memory facts. The manifest content is already established, so keep
                every durable fact it contains; do not judge whether it is still true.

                {BuildFactRules()}

                Additional rules:
                - Drop task and reminder state (task lists, IDs, cron expressions, due dates,
                  confirmations), but keep any lasting preference or routine behind it.
                - sourceTurnIds is always an empty list.
                """;
    }

    private string BuildFactRules()
    {
        return $"""
                Fact rules:
                - One fact per item, written as a standalone sentence about the user that makes sense
                  without the conversation (no "he", "that one", "the thing above").
                - At most {_options.MaxItemLength} characters per fact.
                - Write each fact in the language the user writes in.
                - category is one of: {string.Join(", ", UserMemoryItemCategories.All)}.
                - isCore is true only for facts that should shape every reply: the user's name, how they
                  want to be addressed, their language, strong communication preferences, and key
                  identity facts. Everything else is false.
                """;
    }

    private string BuildTurnExtractionInput(IReadOnlyList<MemoryExtractionTurn> turns)
    {
        var builder = new StringBuilder();
        foreach (var turn in turns)
        {
            var localTime = assistantTimeService.FormatUtcForDisplay(
                turn.CreatedAtUtc,
                assistantTimeService.DefaultTimeZoneId,
                "yyyy-MM-dd HH:mm");

            builder.AppendLine($"""<turn id="{turn.Id}" at="{localTime} {assistantTimeService.DefaultTimeZoneId}">""");
            builder.AppendLine($"<user>{turn.UserMessage}</user>");
            builder.AppendLine($"<assistant>{turn.AssistantMessage}</assistant>");
            builder.AppendLine("</turn>");
        }

        return builder.ToString();
    }

    private static string BuildReconcileInstructions()
    {
        return """
               You maintain the long-term memory of a personal Telegram assistant as separate facts.
               Each <candidate> is a new fact about the user. The <existing> items inside it are the
               stored facts most similar to it. Candidates come from newer conversations than the
               existing items.

               Return exactly one decision per candidate, with its candidateIndex and one action:
               - "add": the candidate is new information. targetItemId is 0.
               - "update": the candidate refines or corrects an existing item. targetItemId is that
                 item's id and text is the merged fact.
               - "delete": the user explicitly said an existing item is no longer true and the candidate
                 adds nothing new worth keeping. targetItemId is that item's id.
               - "noop": the candidate is already known. targetItemId is the item that already covers it.

               Rules:
               - targetItemId must be the id of an <existing> item listed under that same candidate.
               - For "add" and "update", fill text, category and isCore for the resulting fact. Keep the
                 candidate's language, category and isCore unless the existing item is a better fit.
               - When facts contradict, the latest explicit user statement wins, so prefer "update".
               - Items that are merely related are not duplicates: two different facts about the same
                 topic are "add", not "update".
               - reason is one short sentence explaining the decision.
               """;
    }

    private string BuildReconcileInput(IReadOnlyList<ReconcileCandidate> candidates)
    {
        var builder = new StringBuilder();
        foreach (var candidate in candidates)
        {
            builder.AppendLine(
                $"""<candidate index="{candidate.Index}" category="{candidate.Fact.Category}" isCore="{FormatBool(candidate.Fact.IsCore)}">""");
            builder.AppendLine(candidate.Fact.Text);

            foreach (var neighbor in candidate.Neighbors)
            {
                var createdLocal = assistantTimeService.FormatUtcForDisplay(
                    neighbor.CreatedAt,
                    assistantTimeService.DefaultTimeZoneId,
                    "yyyy-MM-dd");

                builder.AppendLine(
                    $"""<existing id="{neighbor.Id}" category="{neighbor.Category}" isCore="{FormatBool(neighbor.IsCore)}" created="{createdLocal}">{neighbor.Text}</existing>""");
            }

            builder.AppendLine("</candidate>");
        }

        return builder.ToString();
    }

    private static string FormatBool(bool value) => value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();

    // Structured output root objects: some providers reject a JSON schema whose root is an array.
    private sealed record CandidateFactList(IReadOnlyList<CandidateFact> Facts);

    private sealed record MemoryDecisionList(IReadOnlyList<MemoryDecision> Decisions);
}
