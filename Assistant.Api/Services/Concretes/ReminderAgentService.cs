using Assistant.Api.Domain.Configurations;
using Assistant.Api.Domain.Dtos;
using Assistant.Api.Services.Abstracts;
using Google.GenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Services.Concretes;

public class ReminderAgentService(
    IReminderSchedulerService reminderSchedulerService,
    IOptions<AiOptions> aiOptions,
    ILogger<ReminderAgentService> logger,
    ILogger<ReminderToolFunctions> reminderToolLogger
) : IReminderAgentService
{
    public async Task<ReminderAgentResponse> ProcessReminderAsync(
        long chatId,
        string userInput,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return new ReminderAgentResponse(false, BuildUsageMessage());
        }

        if (!TryResolveTimeZone(aiOptions.Value.DefaultTimeZoneId, out var timeZoneInfo))
        {
            logger.LogWarning(
                "Configured timezone is invalid. Falling back to UTC. TimezoneId: {TimeZoneId}",
                aiOptions.Value.DefaultTimeZoneId
            );
            timeZoneInfo = TimeZoneInfo.Utc;
        }

        var reminderToolFunctions = new ReminderToolFunctions(chatId, reminderSchedulerService, reminderToolLogger);
        var tools = new List<AITool> { AIFunctionFactory.Create(reminderToolFunctions.CreateReminder) };

        try
        {
            var geminiClient = new Client(apiKey: aiOptions.Value.GoogleApiKey);
            var chatClient = geminiClient
                .AsIChatClient(aiOptions.Value.Model)
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();
            
            var agent = new ChatClientAgent(
                chatClient,
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        Instructions = BuildSystemPrompt(timeZoneInfo.Id),
                        Temperature = 1,
                        Tools = tools,
                        ModelId = "gemini-3.1-flash-lite-preview"
                    }
                }
            );
            
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZoneInfo);
            var agentInput = $"""
                             Current local datetime in {timeZoneInfo.Id}: {nowLocal:yyyy-MM-dd HH:mm:ss}
                             User request: {userInput}
                             """;

            var response = await agent.RunAsync(agentInput, cancellationToken: cancellationToken);
            var responseText = response.Text?.Trim() ?? string.Empty;

            if (reminderToolFunctions.LastResult is not null)
            {
                if (reminderToolFunctions.LastResult.Status == ReminderToolStatuses.Created)
                {
                    var createdMessage = reminderToolFunctions.LastResult.HumanSummary;
                    if (!string.IsNullOrWhiteSpace(createdMessage))
                    {
                        return new ReminderAgentResponse(true, responseText);
                    }

                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        return new ReminderAgentResponse(true, responseText);
                    }

                    return new ReminderAgentResponse(true, "Hatırlatma oluşturuldu.");
                }

                var invalidMessage = BuildParseErrorMessage(reminderToolFunctions.LastResult.Error);
                return new ReminderAgentResponse(false, invalidMessage);
            }

            if (!string.IsNullOrWhiteSpace(responseText))
            {
                return new ReminderAgentResponse(false, responseText);
            }

            return new ReminderAgentResponse(false, BuildParseErrorMessage(null));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reminder agent execution failed.");
            return new ReminderAgentResponse(
                false,
                "Hatırlatma oluşturulamadı. Lütfen tekrar dener misin?"
            );
        }
    }

    private static string BuildSystemPrompt(string timeZoneId)
    {
        return $"""
                You are a reminder scheduling assistant for a Telegram bot.
                Your only goal is to call the CreateReminder function exactly once when the request is clear.

                Rules:
                - Interpret all relative times in timezone: {timeZoneId}.
                - For one-time reminder set isRecurring=false, provide runAtLocalIso, set cronExpression=null.
                - For recurring reminder set isRecurring=true, provide 5-field cronExpression, set runAtLocalIso=null.

                Reminder text rules:
                - reminderText must be the message that will be shown to the user.
                - Do NOT repeat the user's command like "hatırlat".
                - Convert the request into a natural reminder sentence in Turkish.
                - Remove phrases like "bana hatırlat".
                - Example: "5 dakika sonra su içmeyi hatırlat" → "Su içmeyi unutma."

                - Keep reminderText concise and user-ready in Turkish.
                - If time expression is ambiguous or cannot be resolved, do NOT call the function.
                - If you do not call the function, answer shortly and ask for a clearer time expression.
                - Never invent or guess unclear date/time values.
                """;
    }

    private static string BuildParseErrorMessage(string? error)
    {
        var details = string.IsNullOrWhiteSpace(error)
            ? "Zaman ifadesi net anlaşılamadı."
            : error;

        return $"""
                {details}

                Örnek kullanımlar:
                /remind 5 saat sonra Mustafa abiyle toplantımı hatırlat
                /remind her gün saat 17:00 su içmeyi hatırlat
                /remind her pazartesi 09:30 haftalık planı hatırlat
                """;
    }

    private static string BuildUsageMessage()
    {
        return """
               Kullanım: /remind <hatırlatma isteği>

               Örnek:
               /remind 5 saat sonra Mustafa abiyle toplantımı hatırlat
               """;
    }

    private static bool TryResolveTimeZone(string timeZoneId, out TimeZoneInfo timeZoneInfo)
    {
        try
        {
            timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            if (string.Equals(timeZoneId, "Europe/Istanbul", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
                    return true;
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch (InvalidTimeZoneException)
        {
            // ignored
        }

        timeZoneInfo = TimeZoneInfo.Utc;
        return false;
    }
}
