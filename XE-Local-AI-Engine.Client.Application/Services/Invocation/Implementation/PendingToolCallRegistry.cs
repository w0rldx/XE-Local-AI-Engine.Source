namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     The one set of tool calls currently parked on an out-of-stream answer, shared by every collaborator that can
///     register, release or observe one.
/// </summary>
/// <remarks>
///     Those are <see cref="ToolApprovalCoordinator" /> for the operator's approve/deny round-trip,
///     <see cref="InvocationLifecycleTracker" /> for cancel and drain, and <see cref="ApiToolCallBridge" /> for the
///     tool-result budget and the stale sweep; <c>WorkSessionExecutionSupervisor</c> only reads it. There is exactly ONE
///     instance per node, handed out by reference: a second copy would let a call be registered in one dictionary and
///     resolved against another, parking the turn until its timeout.
/// </remarks>
public sealed class PendingToolCallRegistry
{
    /// <summary>
    ///     Pending calls keyed by the opaque request id the browser echoes back.
    /// </summary>
    public ConcurrentDictionary<string, PendingToolCall> Calls { get; } = new(StringComparer.Ordinal);
}

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
