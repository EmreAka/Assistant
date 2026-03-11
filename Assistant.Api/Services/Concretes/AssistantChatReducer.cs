using Microsoft.Extensions.AI;

namespace Assistant.Api.Services.Concretes;

#pragma warning disable MEAI001
internal sealed class AssistantChatReducer(
    IChatClient chatClient,
    ILogger logger,
    int recentTokenBudget = 24_000,
    int summarizeThresholdTokenBudget = 36_000) : IChatReducer
{
    private const string SummaryMessageId = "__assistant_chat_summary";
    private const string SummaryHeader = "Conversation summary of earlier turns:";
    private const int MaxSummaryBulletCount = 24;

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
            .Where(HasUsefulText)
            .ToList();

        if (EstimateTokenCount(conversationMessages) <= summarizeThresholdTokenBudget)
        {
            return materializedMessages;
        }

        var recentMessages = TakeRecentMessagesWithinBudget(conversationMessages, recentTokenBudget);
        var olderMessages = conversationMessages
            .Take(Math.Max(0, conversationMessages.Count - recentMessages.Count))
            .ToList();

        if (olderMessages.Count == 0)
        {
            return materializedMessages;
        }

        var summaryResult = await SummarizeAsync(existingSummary?.Text, olderMessages, cancellationToken);
        if (summaryResult.KeepOriginalMessages || summaryResult.SummaryMessage is null)
        {
            return materializedMessages;
        }

        return BuildReducedMessages(systemMessages, summaryResult.SummaryMessage, recentMessages);
    }

    private async Task<SummaryReductionResult> SummarizeAsync(
        string? existingSummaryText,
        IReadOnlyList<ChatMessage> olderMessages,
        CancellationToken cancellationToken)
    {
        var transcript = BuildTranscript(olderMessages);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return SummaryReductionResult.KeepOriginal();
        }

        var existingSummaryBody = ExtractSummaryBody(existingSummaryText);

        var summarizationMessages = new List<ChatMessage>
        {
            new(
                ChatRole.System,
                $$"""
                  You compress older chat history for a personal Telegram assistant.
                  Return plain text only.

                  Preserve only information future turns are likely to need.

                  Preferred sections:
                  [Open loops]
                  [Decisions and commitments]
                  [User preferences and profile]
                  [Important entities and references]
                  [Recent conversation themes]

                  Rules:
                  - Use only sections that have content.
                  - Use bullet lines starting with "- ".
                  - Keep wording precise when a user gave a clear instruction or correction.
                  - Keep chronology only when timing matters later.
                  - Do not invent facts.
                  - Do not include raw tool syntax or function-call traces.
                  - Keep at most {{MaxSummaryBulletCount}} bullets total across all sections.
                  """),
            new(ChatRole.User, BuildSummarizationInput(existingSummaryBody, transcript))
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
                logger.LogWarning("Chat history summarization returned an empty summary. Keeping original messages.");
                return SummaryReductionResult.KeepOriginal();
            }

            return SummaryReductionResult.Use(CreateSummaryMessage(normalizedSummary));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Chat history summarization failed. Keeping original messages.");
            return SummaryReductionResult.KeepOriginal();
        }
    }

    private static List<ChatMessage> BuildReducedMessages(
        IEnumerable<ChatMessage> systemMessages,
        ChatMessage summaryMessage,
        IEnumerable<ChatMessage> recentMessages)
    {
        var reducedMessages = new List<ChatMessage>();
        reducedMessages.AddRange(systemMessages);
        reducedMessages.Add(summaryMessage);
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
            messages.Select(message => $"{message.Role.Value.ToUpperInvariant()}: {NormalizeLine(message.Text)}"));
    }

    private static List<ChatMessage> TakeRecentMessagesWithinBudget(
        IReadOnlyList<ChatMessage> messages,
        int tokenBudget)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var selected = new List<ChatMessage>();
        var consumedTokens = 0;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            var messageTokens = EstimateTokenCount(message);

            if (selected.Count > 0 && consumedTokens + messageTokens > tokenBudget)
            {
                break;
            }

            selected.Add(message);
            consumedTokens += messageTokens;
        }

        selected.Reverse();
        return selected;
    }

    private static int EstimateTokenCount(IEnumerable<ChatMessage> messages)
    {
        return messages.Sum(EstimateTokenCount);
    }

    private static int EstimateTokenCount(ChatMessage message)
    {
        var text = message.Text ?? string.Empty;
        return Math.Max(1, (text.Length + 3) / 4);
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

    private static string NormalizeLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static ChatMessage CreateSummaryMessage(string summary)
    {
        return new ChatMessage(ChatRole.Assistant, $"{SummaryHeader}\n{summary}")
        {
            MessageId = SummaryMessageId
        };
    }

    private readonly record struct SummaryReductionResult(ChatMessage? SummaryMessage, bool KeepOriginalMessages)
    {
        public static SummaryReductionResult KeepOriginal() => new(null, true);

        public static SummaryReductionResult Use(ChatMessage summaryMessage) => new(summaryMessage, false);
    }
}
#pragma warning restore MEAI001
