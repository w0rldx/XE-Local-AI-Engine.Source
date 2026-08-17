namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Models.Events;

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
    ///     Pending calls keyed by the opaque request id the browser/hub echoes back.
    /// </summary>
    public ConcurrentDictionary<string, PendingToolCall> Calls { get; } = new(StringComparer.Ordinal);
}

/// <summary>
///     A tool call awaiting an out-of-stream answer: first the approval decision (when the call is approval-gated),
///     then the tool result itself.
/// </summary>
public sealed record PendingToolCall(
    Guid InvocationId,
    DateTimeOffset CreatedAt,
    TaskCompletionSource<bool> ApprovalCompletion,
    TaskCompletionSource<ToolCallResultEvent> ResultCompletion);
