using Hangfire;

namespace Assistant.Api.Features.UserManagement.Services;

public class MemoryConsolidationScheduler(
    IBackgroundJobClient backgroundJobClient
) : IMemoryConsolidationScheduler
{
    public string Enqueue(int telegramUserId)
    {
        return backgroundJobClient.Enqueue<MemoryConsolidationJob>(job =>
            job.ExecuteAsync(telegramUserId));
    }
}
