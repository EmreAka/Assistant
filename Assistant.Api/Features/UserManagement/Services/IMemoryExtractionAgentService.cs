namespace Assistant.Api.Features.UserManagement.Services;

public interface IMemoryExtractionAgentService
{
    Task<IReadOnlyList<CandidateFact>> ExtractFromTurnsAsync(IReadOnlyList<MemoryExtractionTurn> turns, CancellationToken cancellationToken);
    Task<IReadOnlyList<CandidateFact>> ExtractFromManifestAsync(string manifest, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryDecision>> ReconcileAsync(IReadOnlyList<ReconcileCandidate> candidates, CancellationToken cancellationToken);
}

public sealed record MemoryExtractionTurn(
    int Id,
    string UserMessage,
    string AssistantMessage,
    DateTime CreatedAtUtc);

public sealed record CandidateFact(
    string Text,
    string Category,
    bool IsCore,
    int[] SourceTurnIds);

public sealed record ReconcileCandidate(
    int Index,
    CandidateFact Fact,
    IReadOnlyList<MemoryItemSearchResult> Neighbors);

// No nullable fields: TargetItemId is 0 for "add", and Text/Category/IsCore are ignored for
// "delete" and "noop". This keeps the JSON schema simple for every provider.
public sealed record MemoryDecision(
    int CandidateIndex,
    string Action,
    int TargetItemId,
    string Text,
    string Category,
    bool IsCore,
    string Reason);

public static class MemoryDecisionActions
{
    public const string Add = "add";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Noop = "noop";
}
