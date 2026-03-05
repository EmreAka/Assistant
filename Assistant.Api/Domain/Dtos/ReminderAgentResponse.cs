namespace Assistant.Api.Domain.Dtos;

public record ReminderAgentResponse(
    bool IsSuccess,
    string UserMessage
);
