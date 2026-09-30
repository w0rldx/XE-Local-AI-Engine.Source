namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

/// <summary>
///     A tool call parked on the operator's approval decision.
/// </summary>
public sealed class PendingToolCall
{
    public required Guid InvocationId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required TaskCompletionSource<bool> ApprovalCompletion { get; init; }

    /// <summary>The tool the approval waits on, so the stale sweep can name it when the approval expires.</summary>
    public string? ToolName { get; init; }
}
