using System.Numerics.Tensors;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Features.Chat.Services;
using Assistant.Api.Features.UserManagement.Models;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Assistant.Api.Features.UserManagement.Services;

[AutomaticRetry(Attempts = 2)]
[DisableConcurrentExecution(timeoutInSeconds: 10 * 60)]
public class MemoryExtractionJob(
    ApplicationDbContext dbContext,
    IMemoryService memoryService,
    IMemoryItemService memoryItemService,
    IMemoryExtractionAgentService agentService,
    IChatTurnEmbeddingService embeddingService,
    IBackgroundJobClient backgroundJobClient,
    IOptions<MemoryItemOptions> options,
    ILogger<MemoryExtractionJob> logger
)
{
    // Candidates this close to an item added earlier in the same run are treated as duplicates.
    // Pending inserts aren't visible to FindNeighborsAsync, so this check runs in memory.
    private const double DuplicateMaxCosineDistance = 0.05;

    private readonly MemoryItemOptions _options = options.Value;
    private int _activeCoreCount;

    public async Task ExecuteAsync(int telegramUserId)
    {
        await ImportManifestIfNeededAsync(telegramUserId);

        // A NULL MemoryProcessedAt means "not extracted yet", so the column itself is the work queue.
        var turns = await dbContext.ChatTurns
            .Where(x => x.TelegramUserId == telegramUserId && x.MemoryProcessedAt == null)
            .OrderBy(x => x.Id)
            .Take(_options.MaxTurnsPerRun)
            .ToListAsync();

        if (turns.Count == 0)
        {
            return;
        }

        var turnIds = turns.Select(x => x.Id).ToHashSet();
        var extracted = await agentService.ExtractFromTurnsAsync(
            turns.Select(x => new MemoryExtractionTurn(x.Id, x.UserMessage, x.AssistantMessage, x.CreatedAt)).ToList(),
            CancellationToken.None);

        var candidates = extracted
            .Select(x => ValidateFact(x, turnIds))
            .OfType<CandidateFact>()
            .Take(_options.MaxCandidatesPerRun)
            .ToList();

        _activeCoreCount = await CountActiveCoreItemsAsync(telegramUserId);
        var now = DateTime.UtcNow;
        var addedVectors = new List<Vector>();
        var candidateVectors = new Dictionary<int, Vector>();
        var toReconcile = new List<ReconcileCandidate>();
        var directAddCount = 0;

        // One embedding call per candidate: multi-input requests are rejected by the OpenRouter ZDR guardrail.
        for (var index = 0; index < candidates.Count; index++)
        {
            var fact = candidates[index];
            var vector = await embeddingService.EmbedDocumentAsync(fact.Text, CancellationToken.None);

            if (IsNearDuplicate(vector, addedVectors))
            {
                continue;
            }

            var neighbors = await memoryItemService.FindNeighborsAsync(telegramUserId, vector, CancellationToken.None);
            if (neighbors.Count == 0)
            {
                AddItem(telegramUserId, fact, vector, "new fact", now);
                addedVectors.Add(vector);
                directAddCount++;
                continue;
            }

            candidateVectors[index] = vector;
            toReconcile.Add(new ReconcileCandidate(index, fact, neighbors));
        }

        var decisions = await agentService.ReconcileAsync(toReconcile, CancellationToken.None);
        var appliedCount = await ApplyDecisionsAsync(telegramUserId, toReconcile, candidateVectors, decisions, addedVectors, now);

        foreach (var turn in turns)
        {
            turn.MemoryProcessedAt = now;
        }

        // Single save: a failure anywhere above leaves the turns NULL, so a retry redoes the whole batch.
        await dbContext.SaveChangesAsync();

        logger.LogInformation(
            "Memory extraction completed. TelegramUserId: {TelegramUserId}, Turns: {Turns}, Candidates: {Candidates}, DirectAdds: {DirectAdds}, Reconciled: {Reconciled}, AppliedDecisions: {AppliedDecisions}",
            telegramUserId,
            turns.Count,
            candidates.Count,
            directAddCount,
            toReconcile.Count,
            appliedCount);

        var remainingCount = await dbContext.ChatTurns
            .CountAsync(x => x.TelegramUserId == telegramUserId && x.MemoryProcessedAt == null);

        if (remainingCount >= _options.TurnsThreshold)
        {
            backgroundJobClient.Enqueue<MemoryExtractionJob>(job => job.ExecuteAsync(telegramUserId));
        }
    }

    // One-time seed from the old manifest, so memory isn't empty after switching to memory items.
    // Runs only while the user has no items at all (any status), so it never runs twice.
    private async Task ImportManifestIfNeededAsync(int telegramUserId)
    {
        if (await memoryItemService.HasAnyItemsAsync(telegramUserId, CancellationToken.None))
        {
            return;
        }

        var chatId = await dbContext.TelegramUsers
            .AsNoTracking()
            .Where(x => x.Id == telegramUserId)
            .Select(x => x.ChatId)
            .FirstOrDefaultAsync();

        var manifest = await memoryService.GetActiveManifestRecordAsync(chatId, CancellationToken.None);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Content))
        {
            return;
        }

        var facts = (await agentService.ExtractFromManifestAsync(manifest.Content, CancellationToken.None))
            .Select(x => ValidateFact(x, allowedTurnIds: null))
            .OfType<CandidateFact>()
            .ToList();

        _activeCoreCount = 0;
        var now = DateTime.UtcNow;
        var addedVectors = new List<Vector>();

        foreach (var fact in facts)
        {
            var vector = await embeddingService.EmbedDocumentAsync(fact.Text, CancellationToken.None);
            if (IsNearDuplicate(vector, addedVectors))
            {
                continue;
            }

            AddItem(telegramUserId, fact, vector, $"imported from manifest v{manifest.Version}", now);
            addedVectors.Add(vector);
        }

        await dbContext.SaveChangesAsync();

        if (addedVectors.Count == 0)
        {
            // Nothing was saved, so the next run will try the import again.
            logger.LogWarning(
                "Manifest import produced no memory items. TelegramUserId: {TelegramUserId}, ManifestVersion: {ManifestVersion}",
                telegramUserId,
                manifest.Version);
            return;
        }

        logger.LogInformation(
            "Imported memory manifest. TelegramUserId: {TelegramUserId}, ManifestVersion: {ManifestVersion}, Items: {Items}",
            telegramUserId,
            manifest.Version,
            addedVectors.Count);
    }

    private async Task<int> ApplyDecisionsAsync(
        int telegramUserId,
        IReadOnlyList<ReconcileCandidate> toReconcile,
        IReadOnlyDictionary<int, Vector> candidateVectors,
        IReadOnlyList<MemoryDecision> decisions,
        List<Vector> addedVectors,
        DateTime now)
    {
        // The first decision per candidate wins; extra ones are ignored.
        var decisionsByIndex = decisions
            .GroupBy(x => x.CandidateIndex)
            .ToDictionary(x => x.Key, x => x.First());

        // Each existing item can be updated or deleted at most once per run.
        var changedItemIds = new HashSet<int>();
        var appliedCount = 0;

        foreach (var candidate in toReconcile)
        {
            if (!decisionsByIndex.TryGetValue(candidate.Index, out var decision))
            {
                logger.LogWarning("No reconcile decision returned for candidate. Candidate: {Candidate}", candidate.Fact.Text);
                continue;
            }

            var action = decision.Action?.Trim().ToLowerInvariant();
            var targetIsOffered = candidate.Neighbors.Any(x => x.Id == decision.TargetItemId);

            if (action != MemoryDecisionActions.Add && (!targetIsOffered || changedItemIds.Contains(decision.TargetItemId)))
            {
                logger.LogWarning(
                    "Dropping reconcile decision with an invalid target. Action: {Action}, TargetItemId: {TargetItemId}, Candidate: {Candidate}",
                    decision.Action,
                    decision.TargetItemId,
                    candidate.Fact.Text);
                continue;
            }

            var applied = action switch
            {
                MemoryDecisionActions.Add => await AddFromDecisionAsync(telegramUserId, candidate, candidateVectors[candidate.Index], decision, addedVectors, now),
                MemoryDecisionActions.Update => await UpdateAsync(telegramUserId, candidate, candidateVectors[candidate.Index], decision, now),
                MemoryDecisionActions.Delete => await DeleteAsync(telegramUserId, decision, now),
                MemoryDecisionActions.Noop => await ConfirmAsync(telegramUserId, decision.TargetItemId, now),
                _ => false
            };

            if (!applied)
            {
                logger.LogWarning(
                    "Reconcile decision was not applied. Action: {Action}, TargetItemId: {TargetItemId}, Candidate: {Candidate}",
                    decision.Action,
                    decision.TargetItemId,
                    candidate.Fact.Text);
                continue;
            }

            if (action is MemoryDecisionActions.Update or MemoryDecisionActions.Delete)
            {
                changedItemIds.Add(decision.TargetItemId);
            }

            appliedCount++;
        }

        return appliedCount;
    }

    private async Task<bool> AddFromDecisionAsync(
        int telegramUserId,
        ReconcileCandidate candidate,
        Vector candidateVector,
        MemoryDecision decision,
        List<Vector> addedVectors,
        DateTime now)
    {
        var fact = ValidateFact(new CandidateFact(decision.Text, decision.Category, decision.IsCore, candidate.Fact.SourceTurnIds), allowedTurnIds: null);
        if (fact is null)
        {
            return false;
        }

        var vector = await GetVectorAsync(fact.Text, candidate.Fact.Text, candidateVector);
        if (IsNearDuplicate(vector, addedVectors))
        {
            return true;
        }

        AddItem(telegramUserId, fact, vector, decision.Reason, now);
        addedVectors.Add(vector);
        return true;
    }

    // UPDATE keeps history: the old row is superseded and points to the new one.
    private async Task<bool> UpdateAsync(
        int telegramUserId,
        ReconcileCandidate candidate,
        Vector candidateVector,
        MemoryDecision decision,
        DateTime now)
    {
        var target = await FindActiveItemAsync(telegramUserId, decision.TargetItemId);
        var fact = ValidateFact(
            new CandidateFact(decision.Text, decision.Category, decision.IsCore, candidate.Fact.SourceTurnIds),
            allowedTurnIds: null);

        if (target is null || fact is null)
        {
            return false;
        }

        var vector = await GetVectorAsync(fact.Text, candidate.Fact.Text, candidateVector);

        if (target.IsCore)
        {
            _activeCoreCount--;
        }

        var replacement = AddItem(
            telegramUserId,
            fact with { SourceTurnIds = target.SourceTurnIds.Union(fact.SourceTurnIds).ToArray() },
            vector,
            decision.Reason,
            now);

        target.Status = UserMemoryItemStatuses.Superseded;
        target.SupersededBy = replacement;
        target.UpdatedAt = now;
        return true;
    }

    private async Task<bool> DeleteAsync(int telegramUserId, MemoryDecision decision, DateTime now)
    {
        var target = await FindActiveItemAsync(telegramUserId, decision.TargetItemId);
        if (target is null)
        {
            return false;
        }

        if (target.IsCore)
        {
            _activeCoreCount--;
        }

        target.Status = UserMemoryItemStatuses.Deleted;
        target.ChangeReason = decision.Reason ?? string.Empty;
        target.UpdatedAt = now;
        return true;
    }

    private async Task<bool> ConfirmAsync(int telegramUserId, int targetItemId, DateTime now)
    {
        var target = await FindActiveItemAsync(telegramUserId, targetItemId);
        if (target is null)
        {
            return false;
        }

        target.LastConfirmedAt = now;
        return true;
    }

    private UserMemoryItem AddItem(int telegramUserId, CandidateFact fact, Vector vector, string? reason, DateTime now)
    {
        var isCore = fact.IsCore;
        if (isCore && _activeCoreCount >= _options.MaxCoreItems)
        {
            logger.LogWarning(
                "Core memory item limit reached; storing as non-core. TelegramUserId: {TelegramUserId}, Text: {Text}",
                telegramUserId,
                fact.Text);
            isCore = false;
        }

        if (isCore)
        {
            _activeCoreCount++;
        }

        var item = new UserMemoryItem
        {
            TelegramUserId = telegramUserId,
            Text = fact.Text,
            Category = fact.Category,
            IsCore = isCore,
            Status = UserMemoryItemStatuses.Active,
            Embedding = vector,
            SourceTurnIds = fact.SourceTurnIds,
            ChangeReason = reason ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            LastConfirmedAt = now
        };

        dbContext.UserMemoryItems.Add(item);
        return item;
    }

    // Reuses the candidate's vector when the model kept its text, saving an embedding call.
    private Task<Vector> GetVectorAsync(string text, string candidateText, Vector candidateVector)
    {
        return string.Equals(text, candidateText, StringComparison.Ordinal)
            ? Task.FromResult(candidateVector)
            : embeddingService.EmbedDocumentAsync(text, CancellationToken.None);
    }

    private Task<UserMemoryItem?> FindActiveItemAsync(int telegramUserId, int itemId)
    {
        return dbContext.UserMemoryItems.FirstOrDefaultAsync(x =>
            x.Id == itemId && x.TelegramUserId == telegramUserId && x.Status == UserMemoryItemStatuses.Active);
    }

    private Task<int> CountActiveCoreItemsAsync(int telegramUserId)
    {
        return dbContext.UserMemoryItems.CountAsync(x =>
            x.TelegramUserId == telegramUserId && x.Status == UserMemoryItemStatuses.Active && x.IsCore);
    }

    // Model output is untrusted: trim it, enforce the length limit, map unknown categories to
    // "other", and keep only source turn IDs that were actually in the batch.
    private CandidateFact? ValidateFact(CandidateFact fact, IReadOnlySet<int>? allowedTurnIds)
    {
        var text = fact.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > _options.MaxItemLength)
        {
            logger.LogWarning("Dropping invalid memory fact. Text: {Text}", fact.Text);
            return null;
        }

        var category = fact.Category?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!UserMemoryItemCategories.All.Contains(category))
        {
            category = UserMemoryItemCategories.Other;
        }

        var sourceTurnIds = (fact.SourceTurnIds ?? [])
            .Where(x => allowedTurnIds is null || allowedTurnIds.Contains(x))
            .Distinct()
            .ToArray();

        return new CandidateFact(text, category, fact.IsCore, sourceTurnIds);
    }

    private static bool IsNearDuplicate(Vector vector, IEnumerable<Vector> addedVectors)
    {
        return addedVectors.Any(added =>
            1 - TensorPrimitives.CosineSimilarity(vector.Memory.Span, added.Memory.Span) <= DuplicateMaxCosineDistance);
    }
}
