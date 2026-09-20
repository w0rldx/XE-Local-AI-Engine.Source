namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>Tracks whether a running invocation still has a live event consumer attached — a browser watching it.</summary>
/// <remarks>
///     The hub is the only writer (<c>LocalChatHub</c> wraps each of its four stream entry points), so a run that never
///     streamed over it — a scheduled agent run, a platform-hub run, an MCP agent run — has NO entry here and is
///     invisible to both <see cref="IsDetached" /> and <see cref="ListDetached" />. That asymmetry is the point: only a
///     turn that once had a watcher and lost it counts as detached.
/// </remarks>
public interface IInvocationAttachmentTracker
{
    /// <summary>Registers one attached consumer for <paramref name="invocationId" />.</summary>
    /// <remarks>
    ///     Ref-counted: concurrent consumers — a reconnect racing the original stream — each hold a handle, and the
    ///     invocation becomes detached only when the last one is disposed. Disposing a handle twice is a no-op.
    /// </remarks>
    IDisposable Attach(Guid invocationId);

    /// <summary>
    ///     Whether <paramref name="invocationId" /> was attached at some point and currently has no consumer, and
    ///     <see langword="false" /> for both a currently-attached run and a run that never attached.
    /// </summary>
    /// <remarks>
    ///     Not a plain <c>!IsAttached</c>: detached means "had a watcher and lost it", not "has no watcher". A run that
    ///     never streamed over <c>LocalChatHub</c> has no entry here and must read <see langword="false" />, because
    ///     two consumers act on this answer — <c>SetInvocationDeadline</c> would strip such headless runs of their full
    ///     human-park budget, while <c>DetachedInvocationReaper</c> would still never reap them
    ///     (<see cref="ListDetached" /> is entry-based), leaving the two disagreeing about which runs are abandoned.
    /// </remarks>
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
public sealed record DetachedInvocation
{
    public required Guid InvocationId { get; init; }

    public required DateTimeOffset DetachedAtUtc { get; init; }
}

/// <summary>Carries which invocation changed and whether it is now attached.</summary>
public sealed class InvocationAttachmentChangedEventArgs : EventArgs
{
    public InvocationAttachmentChangedEventArgs(Guid invocationId, bool attached)
    {
        InvocationId = invocationId;
        Attached = attached;
    }

    public Guid InvocationId { get; }

    public bool Attached { get; }
}
