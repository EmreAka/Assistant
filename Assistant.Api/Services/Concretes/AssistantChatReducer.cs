using Microsoft.Extensions.AI;

namespace Assistant.Api.Services.Concretes;

#pragma warning disable MEAI001
internal sealed class AssistantChatReducer(
    IChatClient chatClient,
    ILogger logger,
    int recentMessageCount = 24,
    int summarizeThreshold = 36,
    int maxMessagesToSummarize = 48) : IChatReducer
{
    private const string SummaryMessageId = "__assistant_chat_summary";
    private const string SummaryHeader = "Conversation summary of earlier turns:";

    public async Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var materializedMessages = messages.ToList();
        if (materializedMessages.Count == 0)
        {
            return [];
        }

        var systemMessages = materializedMessages
            .Where(message => message.Role == ChatRole.System)
            .ToList();

        var existingSummary = materializedMessages
            .FirstOrDefault(IsSummaryMessage);

        var conversationMessages = materializedMessages
            .Where(message => message.Role != ChatRole.System)
            .Where(message => !IsSummaryMessage(message))
            .Where(message => HasUsefulText(message))
            .ToList();

        if (conversationMessages.Count <= summarizeThreshold)
        {
            return materializedMessages;
        }

        var recentMessages = conversationMessages
            .TakeLast(recentMessageCount)
            .ToList();

        var olderMessages = conversationMessages
            .Take(Math.Max(0, conversationMessages.Count - recentMessages.Count))
            .TakeLast(maxMessagesToSummarize)
            .ToList();

        if (olderMessages.Count == 0)
        {
            return materializedMessages;
        }

        var summaryMessage = await SummarizeAsync(
            ExtractSummaryBody(existingSummary?.Text),
            olderMessages,
            cancellationToken);

        if (summaryMessage is null)
        {
            return BuildReducedMessages(systemMessages, existingSummary, recentMessages);
        }

        return BuildReducedMessages(systemMessages, summaryMessage, recentMessages);
    }

    private async Task<ChatMessage?> SummarizeAsync(
        string? existingSummary,
        IReadOnlyList<ChatMessage> olderMessages,
        CancellationToken cancellationToken)
    {
        var transcript = BuildTranscript(olderMessages);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return string.IsNullOrWhiteSpace(existingSummary)
                ? null
                : CreateSummaryMessage(existingSummary);
        }

        var summarizationMessages = new List<ChatMessage>
        {
            new(
                ChatRole.System,
                """
                You compress older chat history for a Telegram assistant.
                Return plain text only.

                Preserve:
                - unresolved user requests
                - decisions already made
                - constraints, preferences, and corrections relevant to the ongoing conversation
                - names, entities, and references the next turns may depend on
                - promised follow-ups and open loops

                Rules:
                - do not invent facts
                - do not include tool syntax or raw function-call details
                - keep chronology only when it matters for future turns
                - keep the summary compact and high-signal
                - prefer short bullet lines starting with '- '
                - output at most 8 bullets
                """),
            new(ChatRole.User, BuildSummarizationInput(existingSummary, transcript))
        };

        try
        {
            var response = await chatClient.GetResponseAsync(
                summarizationMessages,
                new ChatOptions
                {
                    Temperature = 0.2f
                },
                cancellationToken);

            var normalizedSummary = NormalizeSummary(response.Text);
            if (string.IsNullOrWhiteSpace(normalizedSummary))
            {
                normalizedSummary = NormalizeSummary(existingSummary);
            }

            return string.IsNullOrWhiteSpace(normalizedSummary)
                ? null
                : CreateSummaryMessage(normalizedSummary);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Chat history summarization failed. Falling back to the previous summary if available.");

            var normalizedSummary = NormalizeSummary(existingSummary);
            return string.IsNullOrWhiteSpace(normalizedSummary)
                ? null
                : CreateSummaryMessage(normalizedSummary);
        }
    }

    private static List<ChatMessage> BuildReducedMessages(
        IEnumerable<ChatMessage> systemMessages,
        ChatMessage? summaryMessage,
        IEnumerable<ChatMessage> recentMessages)
    {
        var reducedMessages = new List<ChatMessage>();
        reducedMessages.AddRange(systemMessages);

        if (summaryMessage is not null)
        {
            reducedMessages.Add(summaryMessage);
        }

        reducedMessages.AddRange(recentMessages);
        return reducedMessages;
    }

    private static string BuildSummarizationInput(string? existingSummary, string transcript)
    {
        if (string.IsNullOrWhiteSpace(existingSummary))
        {
            return $$"""
                     Summarize these older turns for future conversational continuity:

                     {{transcript}}
                     """;
        }

        return $$"""
                 Existing rolling summary:
                 {{existingSummary}}

                 Update that summary using these newer old turns:
                 {{transcript}}
                 """;
    }

    private static string BuildTranscript(IEnumerable<ChatMessage> messages)
    {
        return string.Join(
            Environment.NewLine,
            messages.Select(message => $"{message.Role.Value.ToUpperInvariant()}: {message.Text.Trim()}"));
    }

    private static bool HasUsefulText(ChatMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.Text);
    }

    private static bool IsSummaryMessage(ChatMessage message)
    {
        return string.Equals(message.MessageId, SummaryMessageId, StringComparison.Ordinal);
    }

    private static string? ExtractSummaryBody(string? summaryText)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
        {
            return null;
        }

        if (!summaryText.StartsWith(SummaryHeader, StringComparison.Ordinal))
        {
            return summaryText.Trim();
        }

        return summaryText[SummaryHeader.Length..].Trim();
    }

    private static string? NormalizeSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        return string.Join(
            Environment.NewLine,
            summary
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static ChatMessage CreateSummaryMessage(string summary)
    {
        return new ChatMessage(ChatRole.Assistant, $"{SummaryHeader}\n{summary}")
        {
            MessageId = SummaryMessageId
        };
    }
}
#pragma warning restore MEAI001
