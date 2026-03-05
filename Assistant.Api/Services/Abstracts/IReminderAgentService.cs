using Assistant.Api.Domain.Dtos;

namespace Assistant.Api.Services.Abstracts;

public interface IReminderAgentService
{
    Task<ReminderAgentResponse> ProcessReminderAsync(
        long chatId,
        string userInput,
        CancellationToken cancellationToken
    );
}
