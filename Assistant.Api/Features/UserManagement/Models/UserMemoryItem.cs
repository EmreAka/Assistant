using Pgvector;

namespace Assistant.Api.Features.UserManagement.Models;

public class UserMemoryItem
{
    public int Id { get; set; }
    public int TelegramUserId { get; set; }
    public TelegramUser TelegramUser { get; set; } = null!;
    public string Text { get; set; } = string.Empty;
    public string Category { get; set; } = UserMemoryItemCategories.Other;
    public bool IsCore { get; set; }
    public string Status { get; set; } = UserMemoryItemStatuses.Active;
    // Document-prefixed embedding, filled before insert so active items always have one.
    public Vector Embedding { get; set; } = null!;
    public int[] SourceTurnIds { get; set; } = [];
    public int? SupersededById { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastConfirmedAt { get; set; }
}

public static class UserMemoryItemStatuses
{
    public const string Active = "active";
    public const string Superseded = "superseded";
    public const string Deleted = "deleted";
}

public static class UserMemoryItemCategories
{
    public const string Identity = "identity";
    public const string Preference = "preference";
    public const string Relationship = "relationship";
    public const string WorkEducation = "work_education";
    public const string Health = "health";
    public const string Goal = "goal";
    public const string Routine = "routine";
    public const string Interest = "interest";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Identity, Preference, Relationship, WorkEducation, Health, Goal, Routine, Interest, Other
    };
}
