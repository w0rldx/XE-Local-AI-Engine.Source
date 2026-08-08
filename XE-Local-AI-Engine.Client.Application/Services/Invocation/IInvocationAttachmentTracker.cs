namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Tracks whether a running invocation still has a live event consumer attached — i.e. whether a browser is
///     actually watching the turn. The hub is the only writer (<c>LocalChatHub</c> wraps each of its four stream
///     entry points), so a run that never streamed over the hub — a scheduled agent run, a platform-hub run, an MCP
///     agent run — has NO entry here at all and is deliberately invisible to both
///     <see cref="IsDetached" /> and <see cref="ListDetached" />. That asymmetry is the point: only a turn that once
///     had a watcher and lost it is treated as detached.
/// </summary>
public interface IInvocationAttachmentTracker
{
    /// <summary>
    ///     Registers one attached consumer for <paramref name="invocationId" />. Ref-counted: concurrent consumers
    ///     (a reconnect racing the original stream) each hold a handle, and the invocation only becomes detached when
    ///     the last one is disposed. Disposing a handle twice is a no-op.
    /// </summary>
    IDisposable Attach(Guid invocationId);

    /// <summary>
    ///     Whether <paramref name="invocationId" /> was attached at some point and currently has no consumer.
    ///     <see langword="false" /> for both a currently-attached run and a run that never attached.
    ///     <para>
    ///         DO NOT "simplify" this to a plain <c>!IsAttached</c>. Detached deliberately means "had a watcher and lost
    ///         it", not "has no watcher". A run that never streamed over <c>LocalChatHub</c> — a scheduled agent run, a
    ///         platform-hub run, an MCP agent run — has no entry here and must read <see langword="false" />, because
    ///         two consumers act on this answer and both would be wrong otherwise:
    ///         <c>InvocationRunner.SetInvocationDeadline</c> would strip those headless runs of their full human-park
    ///         budget, and <c>DetachedInvocationReaper</c> would still never reap them (<see cref="ListDetached" /> is
    ///         entry-based), leaving the deadline and the reaper disagreeing about which runs count as abandoned.
    ///     </para>
    /// </summary>
    bool IsDetached(Guid invocationId);

    /// <summary>Every currently-detached invocation with the instant its last consumer went away.</summary>
    IReadOnlyCollection<DetachedInvocation> ListDetached();

    /// <summary>
    ///     Raised when an invocation gains its first consumer or loses its last. The runner listens so a re-attach
    ///     during a human park restores the full park budget from the moment of re-attach.
    /// </summary>
    event EventHandler<InvocationAttachmentChangedEventArgs>? AttachmentChanged;
}

/// <summary>An invocation with no attached consumer, and when the last one went away.</summary>
public sealed record DetachedInvocation(Guid InvocationId, DateTimeOffset DetachedAtUtc);

/// <summary>Carries which invocation changed and whether it is now attached.</summary>
public sealed class InvocationAttachmentChangedEventArgs(Guid invocationId, bool attached) : EventArgs
{
    public Guid InvocationId { get; } = invocationId;

    public bool Attached { get; } = attached;
}
