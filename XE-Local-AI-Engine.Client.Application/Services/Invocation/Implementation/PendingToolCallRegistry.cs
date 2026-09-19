namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     The one set of tool calls currently parked on an out-of-stream answer, shared by every collaborator that can
///     register, release or sweep one: <see cref="ApiToolCallBridge" /> (a platform tool round-trip),
///     <see cref="ToolApprovalCoordinator" /> (a framework approval round-trip) and <see cref="InvocationRunner" />
///     itself (the tool-result post, and cancel/drain).
///     <para>
///         There is exactly ONE instance per node and it is handed out by reference — a second copy would let a call be
///         registered in one dictionary and resolved against another, parking the turn until its timeout instead of
///         releasing it.
///     </para>
/// </summary>
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
}
