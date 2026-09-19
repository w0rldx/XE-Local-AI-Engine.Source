namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>The typed result of an inbound MCP agent execution.</summary>
public sealed record SpawnOutcome
{
    public required SpawnOutcomeKind Kind { get; init; }

    public required string? FailureCode { get; init; }

    public required string DisplayMessage { get; init; }

    public required string? Content { get; init; }

    public static SpawnOutcome Success(string content) =>
        new() { Kind = SpawnOutcomeKind.Success, FailureCode = null, DisplayMessage = "Completed.", Content = content };

    public static SpawnOutcome Rejected(string failureCode, string displayMessage) =>
        new() { Kind = SpawnOutcomeKind.Rejected, FailureCode = failureCode, DisplayMessage = displayMessage, Content = null };

    public static SpawnOutcome Failed(string failureCode, string displayMessage) =>
        new() { Kind = SpawnOutcomeKind.Failed, FailureCode = failureCode, DisplayMessage = displayMessage, Content = null };

    /// <summary>Preserves the original synchronous <c>run_agent</c> string result contract.</summary>
    public string ToSynchronousResult() =>
        Kind == SpawnOutcomeKind.Success ? Content ?? string.Empty : DisplayMessage;
}

public enum SpawnOutcomeKind
{
    Success,
    Rejected,
    Failed
}
