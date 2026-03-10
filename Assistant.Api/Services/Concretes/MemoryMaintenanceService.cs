using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Api.Data;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Domain.Entities;
using Assistant.Api.Services.Abstracts;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Type = Google.GenAI.Types.Type;

namespace Assistant.Api.Services.Concretes;

public class MemoryMaintenanceService(
    ApplicationDbContext dbContext,
    IEmbeddingService embeddingService,
    IOptions<AiOptions> aiOptions,
    ILogger<MemoryMaintenanceService> logger
) : IMemoryMaintenanceService
{
    private const double ConsolidationSimilarityThreshold = 0.82d;
    private static readonly Regex TimeBoundPattern = new(
        @"\b(today|tomorrow|yesterday|tonight|this week|this month|next|last|monday|tuesday|wednesday|thursday|friday|saturday|sunday|january|february|march|april|may|june|july|august|september|october|november|december|\d{4}|\d{1,2}:\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] PositivePreferenceSignals = ["love", "like", "enjoy", "prefer"];
    private static readonly string[] NegativePreferenceSignals = ["hate", "dislike", "avoid", "don't like", "do not like"];
    private readonly AiOptions _aiOptions = aiOptions.Value;

    public async Task RunNightlyMaintenanceAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var archivedCount = await ArchiveStaleMemoriesAsync(nowUtc, cancellationToken);
        var mergedCount = await ConsolidateMemoriesAsync(nowUtc, cancellationToken);

        logger.LogInformation(
            "Memory maintenance completed. Archived: {ArchivedCount}, MergedClusters: {MergedCount}",
            archivedCount,
            mergedCount);
    }

    private async Task<int> ArchiveStaleMemoriesAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var memories = await dbContext.UserMemories
            .Where(x => x.Status == UserMemoryStatuses.Active)
            .ToListAsync(cancellationToken);

        var archivedCount = 0;
        foreach (var memory in memories)
        {
            if (!ShouldArchive(memory, nowUtc))
            {
                continue;
            }

            memory.Status = UserMemoryStatuses.Archived;
            memory.ArchivedAt = nowUtc;
            memory.LastConsolidatedAt = nowUtc;
            archivedCount++;
        }

        if (archivedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return archivedCount;
    }

    private async Task<int> ConsolidateMemoriesAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var memories = await dbContext.UserMemories
            .Where(x => x.Status == UserMemoryStatuses.Active)
            .Where(x => x.ExpiresAt == null || x.ExpiresAt > nowUtc)
            .Where(x => x.Embedding != null)
            .OrderBy(x => x.TelegramUserId)
            .ThenByDescending(x => x.Importance)
            .ThenByDescending(x => x.LastUsedAt ?? x.CreatedAt)
            .ToListAsync(cancellationToken);

        var mergedClusters = 0;

        foreach (var memoryGroup in memories.GroupBy(x => x.TelegramUserId))
        {
            mergedClusters += await ConsolidateUserMemoriesAsync(memoryGroup.ToList(), nowUtc, cancellationToken);
        }

        return mergedClusters;
    }

    private async Task<int> ConsolidateUserMemoriesAsync(
        List<UserMemory> memories,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (memories.Count < 2)
        {
            return 0;
        }

        var mergedClusters = 0;
        var visited = new HashSet<int>();
        var vectors = memories.ToDictionary(x => x.Id, x => x.Embedding!.ToArray());

        foreach (var memory in memories)
        {
            if (!visited.Add(memory.Id))
            {
                continue;
            }

            var cluster = BuildCluster(memory, memories, vectors, visited);
            if (cluster.Count < 2)
            {
                continue;
            }

            if (ShouldSkipCluster(cluster))
            {
                StampCluster(cluster, nowUtc);
                continue;
            }

            var decision = await MergeClusterAsync(cluster, cancellationToken);
            StampCluster(cluster, nowUtc);

            if (decision is null || decision.NoMerge)
            {
                continue;
            }

            var merged = await ApplyMergeAsync(cluster, decision, nowUtc, cancellationToken);
            if (merged)
            {
                mergedClusters++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return mergedClusters;
    }

    private List<UserMemory> BuildCluster(
        UserMemory seed,
        List<UserMemory> memories,
        IReadOnlyDictionary<int, float[]> vectors,
        HashSet<int> visited)
    {
        var cluster = new List<UserMemory> { seed };
        var queue = new Queue<UserMemory>();
        queue.Enqueue(seed);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var candidate in memories)
            {
                if (visited.Contains(candidate.Id))
                {
                    continue;
                }

                var similarity = CosineSimilarity(vectors[current.Id], vectors[candidate.Id]);
                if (similarity < ConsolidationSimilarityThreshold)
                {
                    continue;
                }

                visited.Add(candidate.Id);
                cluster.Add(candidate);
                queue.Enqueue(candidate);
            }
        }

        return cluster;
    }

    private bool ShouldSkipCluster(List<UserMemory> cluster)
    {
        var contents = cluster
            .Select(x => x.Content.ToLowerInvariant())
            .ToList();

        if (contents.Any(content => TimeBoundPattern.IsMatch(content)))
        {
            return true;
        }

        var hasPositiveSignal = contents.Any(content => PositivePreferenceSignals.Any(content.Contains));
        var hasNegativeSignal = contents.Any(content => NegativePreferenceSignals.Any(content.Contains));
        return hasPositiveSignal && hasNegativeSignal;
    }

    private async Task<MemoryMergeDecision?> MergeClusterAsync(List<UserMemory> cluster, CancellationToken cancellationToken)
    {
        try
        {
            var client = new Client(apiKey: _aiOptions.GoogleApiKey);
            var prompt = BuildMergePrompt(cluster);
            var response = await client.Models.GenerateContentAsync(
                model: string.IsNullOrWhiteSpace(_aiOptions.MemoryMaintenanceModel)
                    ? _aiOptions.Model
                    : _aiOptions.MemoryMaintenanceModel,
                contents: prompt,
                config: new GenerateContentConfig
                {
                    ResponseMimeType = "application/json",
                    ResponseSchema = BuildMergeSchema(),
                    Temperature = 0.2f,
                    MaxOutputTokens = 256
                },
                cancellationToken: cancellationToken);

            var text = response.Candidates?
                .SelectMany(candidate => candidate.Content?.Parts ?? [])
                .Select(part => part.Text)
                .FirstOrDefault(part => !string.IsNullOrWhiteSpace(part));

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var decision = JsonSerializer.Deserialize<MemoryMergeDecision>(text, JsonSerializerOptions);
            if (decision is null || decision.NoMerge)
            {
                return decision;
            }

            decision.Content = MemoryNormalization.NormalizeContent(decision.Content ?? string.Empty);
            decision.Category = MemoryNormalization.NormalizeCategory(decision.Category ?? "fact");
            decision.Importance = Math.Clamp(decision.Importance, 1, 10);
            return decision;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Memory consolidation merge request failed.");
            return null;
        }
    }

    private async Task<bool> ApplyMergeAsync(
        List<UserMemory> cluster,
        MemoryMergeDecision decision,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(decision.Content))
        {
            return false;
        }

        var target = await dbContext.UserMemories
            .FirstOrDefaultAsync(
                x => x.TelegramUserId == cluster[0].TelegramUserId
                    && x.Content == decision.Content
                    && x.Category == decision.Category,
                cancellationToken);

        if (target is null)
        {
            target = new UserMemory
            {
                TelegramUserId = cluster[0].TelegramUserId,
                Category = decision.Category,
                Content = decision.Content,
                Importance = decision.Importance,
                Embedding = await embeddingService.GenerateDocumentEmbeddingAsync(decision.Content, decision.Category, cancellationToken),
                Status = UserMemoryStatuses.Active,
                CreatedAt = nowUtc,
                LastUsedAt = cluster.Max(x => x.LastUsedAt ?? x.CreatedAt),
                LastConsolidatedAt = nowUtc
            };

            dbContext.UserMemories.Add(target);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            target.Status = UserMemoryStatuses.Active;
            target.ArchivedAt = null;
            target.MergedIntoMemoryId = null;
            target.Importance = Math.Max(target.Importance, decision.Importance);
            target.LastUsedAt = MaxDate(target.LastUsedAt ?? target.CreatedAt, cluster.Max(x => x.LastUsedAt ?? x.CreatedAt));
            target.LastConsolidatedAt = nowUtc;

            if (target.Embedding is null)
            {
                target.Embedding = await embeddingService.GenerateDocumentEmbeddingAsync(target.Content, target.Category, cancellationToken);
            }
        }

        foreach (var source in cluster.Where(x => x.Id != target.Id))
        {
            source.Status = UserMemoryStatuses.Merged;
            source.ArchivedAt = null;
            source.MergedIntoMemoryId = target.Id;
            source.LastConsolidatedAt = nowUtc;
        }

        return true;
    }

    private bool ShouldArchive(UserMemory memory, DateTime nowUtc)
    {
        if (memory.Status != UserMemoryStatuses.Active)
        {
            return false;
        }

        if (memory.ExpiresAt is not null && memory.ExpiresAt <= nowUtc)
        {
            return true;
        }

        if (memory.Importance >= 7)
        {
            return false;
        }

        var lastRelevantAt = memory.LastUsedAt ?? memory.CreatedAt;
        var staleDays = (nowUtc - lastRelevantAt).TotalDays;

        if (memory.Importance <= 3)
        {
            return staleDays >= _aiOptions.MemoryArchiveAfterDaysLow;
        }

        if (memory.Importance <= 6)
        {
            return staleDays >= _aiOptions.MemoryArchiveAfterDaysMedium;
        }

        return false;
    }

    private static void StampCluster(IEnumerable<UserMemory> cluster, DateTime nowUtc)
    {
        foreach (var memory in cluster)
        {
            memory.LastConsolidatedAt = nowUtc;
        }
    }

    private static double CosineSimilarity(float[] left, float[] right)
    {
        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude <= double.Epsilon || rightMagnitude <= double.Epsilon)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private static string BuildMergePrompt(IEnumerable<UserMemory> cluster)
    {
        var lines = cluster
            .Select((memory, index) => $"{index + 1}. [{memory.Category}] {memory.Content} (importance={memory.Importance})");

        return $$"""
                 You consolidate long-term user memories for a personal assistant.
                 Return JSON only.

                 Rules:
                 - If the memories are contradictory, time-bound, or should remain separate, return {"no_merge": true}.
                 - Otherwise return {"no_merge": false, "content": "...", "category": "preference|profile|goal|fact", "importance": 1-10}.
                 - content must be a single durable sentence about the user.
                 - Do not invent facts not supported by the inputs.

                 Memories:
                 {{string.Join(System.Environment.NewLine, lines)}}
                 """;
    }

    private static Schema BuildMergeSchema()
    {
        return new Schema
        {
            Type = Type.Object,
            Title = "MemoryMergeDecision",
            Properties = new Dictionary<string, Schema>
            {
                ["no_merge"] = new() { Type = Type.Boolean, Title = "NoMerge" },
                ["content"] = new() { Type = Type.String, Title = "Content" },
                ["category"] = new() { Type = Type.String, Title = "Category" },
                ["importance"] = new() { Type = Type.Integer, Title = "Importance" }
            },
            PropertyOrdering = ["no_merge", "content", "category", "importance"],
            Required = ["no_merge"]
        };
    }

    private static DateTime MaxDate(DateTime left, DateTime right)
    {
        return left >= right ? left : right;
    }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class MemoryMergeDecision
    {
        public bool NoMerge { get; set; }
        public string? Content { get; set; }
        public string Category { get; set; } = "fact";
        public int Importance { get; set; } = 5;
    }
}
