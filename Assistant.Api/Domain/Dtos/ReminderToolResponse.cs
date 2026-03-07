namespace Assistant.Api.Domain.Dtos;

public class ReminderToolResponse
{
    public string Status { get; init; } = string.Empty;
    public string? Error { get; init; }

    public static ReminderToolResponse Created(Guid reminderId)
    {
        return new ReminderToolResponse
        {
            Status = ReminderToolStatuses.Created,
        };
    }

    public static ReminderToolResponse Invalid(string error)
    {
        return new ReminderToolResponse
        {
            Status = ReminderToolStatuses.Invalid,
            Error = error
        };
    }
}

public static class ReminderToolStatuses
{
    public const string Created = "created";
    public const string Invalid = "invalid";
}
