namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>The typed result of an inbound MCP agent execution.</summary>
public sealed record SpawnOutcome(SpawnOutcomeKind Kind, string? FailureCode, string DisplayMessage, string? Content)
{
    public static SpawnOutcome Success(string content) =>
        new(SpawnOutcomeKind.Success, FailureCode: null, "Completed.", content);

    public static SpawnOutcome Rejected(string failureCode, string displayMessage) =>
        new(SpawnOutcomeKind.Rejected, failureCode, displayMessage, Content: null);

    public static SpawnOutcome Failed(string failureCode, string displayMessage) =>
        new(SpawnOutcomeKind.Failed, failureCode, displayMessage, Content: null);

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
