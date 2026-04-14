namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryConsolidationScheduler
{
    string Enqueue(int telegramUserId);
}
