namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryConsolidationAgentService
{
    Task<string> ConsolidateAsync(MemoryConsolidationRequest request, CancellationToken cancellationToken);
}

public sealed record MemoryConsolidationRequest(
    long ChatId,
    string CurrentManifest,
    IReadOnlyList<MemoryConsolidationTurn> Turns);

public sealed record MemoryConsolidationTurn(
    string UserMessage,
    string AssistantMessage,
    DateTime CreatedAtUtc);
