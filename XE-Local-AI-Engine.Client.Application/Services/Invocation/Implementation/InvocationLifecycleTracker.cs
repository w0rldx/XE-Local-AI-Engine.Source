namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The active invocation's lifecycle state machine: registration and completion tracking, the whole-turn watchdog
///     and its human-park re-arm, deliberate cancellation, shutdown drain, and the attribution of whatever cancellation
///     ended a turn.
///     <para>
///         Everything here is guarded by ONE lock (<see cref="_syncRoot" />). That is the point of the type: the
///         cancellation origin must be derived from the same synchronized fields the cancel requesters write, in one
///         acquisition, never from a <c>CancellationToken.Register</c> callback (token callbacks run in reverse
///         registration order, so a callback registered at invocation registration is invoked AFTER every later
///         registration and the released agent can reach the failure mapping before it ever ran — which reported a
///         genuine watchdog timeout as a plain cancellation). Splitting these fields across two locks, or snapshotting
///         them into locals earlier than the mapping, reintroduces exactly that class of bug.
///     </para>
///     <para>
///         Not interfaced: there is a single implementation and the state is the runner's own. Public only because
///         <see cref="InvocationRunner" />'s constructor is public and DI activation requires it; nothing outside the
///         runner constructs or calls this. A singleton for the same reason the runner is — it holds the live turn, and
///         cancels/results arrive on other call stacks. It shares the one <see cref="PendingToolCallRegistry" /> with
///         the runner and its other collaborators, so a cancel here releases the calls those registered.
///     </para>
/// </summary>
public sealed class InvocationLifecycleTracker
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _activeInvocationCompletions = new();

    private readonly IInvocationAttachmentTracker _attachmentTracker;

    private readonly TimeSpan _maxPendingToolCallAge;

    // The SAME dictionary instance InvocationRunner, ToolApprovalCoordinator and ApiToolCallBridge hold (see
    // PendingToolCallRegistry): the cancel/drain path below must observe the calls those registered.
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls;

    private readonly Lock _syncRoot = new();

    private Guid? _currentInvocationId;

    // Set once (never reset) when shutdown drain begins, guarded by _syncRoot. A local invocation that reaches
    // admission after this is set is rejected: it registers AFTER the drain snapshot and would otherwise
    // become an untracked active run the drain never waits for.
    private bool _draining;

    // The caller/host token the active invocation's source is linked to (see RegisterActiveInvocation), captured so a
    // cancellation can be attributed to the caller rather than to the invocation watchdog WITHOUT relying on a token
    // callback: callbacks run in reverse registration order, so anything registered by the streaming agent after the
    // runner's own registration is released FIRST and can reach the failure mapping before an earlier callback ran.
    private CancellationToken _hostCancellationToken;

    private CancellationTokenSource? _invocationCancellationTokenSource;

    // The active turn's whole-turn budget, retained so the deadline can be RE-ARMED around a human round-trip
    // (see SetInvocationDeadline). Written and read only under _syncRoot, alongside the source it arms.
    private TimeSpan _invocationTimeout;

    // Whether the active turn is currently parked waiting on a human (a tool approval or an ask_user question).
    // Written and read only under _syncRoot. It exists so the AttachmentChanged handler can re-apply the deadline for a
    // park it did not itself start — a client re-attaching mid-park must get the full park budget back from that moment.
    private bool _parkedOnHuman;

    // Why the active invocation was DELIBERATELY cancelled, recorded synchronously under _syncRoot by the requester
    // itself (Cancel / CancelAll). Unknown means nobody asked: the cancellation then came from the invocation's own
    // CancelAfter watchdog or from the linked caller token, and both are read off observable state at mapping time.
    private CancellationOrigin _requestedCancellationOrigin;

    public InvocationLifecycleTracker(IInvocationAttachmentTracker attachmentTracker,
        PendingToolCallRegistry pendingToolCallRegistry,
        INodeRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(pendingToolCallRegistry);
        _pendingToolCalls = pendingToolCallRegistry.Calls;
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _maxPendingToolCallAge = TimeSpan.FromMinutes(runtimeSettings.GetMaxPendingToolCallAgeMinutes());

        // Subscribe for the process lifetime; both are singletons, so there is no unsubscribe path (mirrors
        // InvocationResumeRegistry's subscription to the same dispatcher).
        _attachmentTracker = attachmentTracker ?? throw new ArgumentNullException(nameof(attachmentTracker));
        _attachmentTracker.AttachmentChanged += OnAttachmentChanged;
    }

    public int ActiveInvocationCount => _activeInvocationCompletions.Count;

    public async Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Fence local admission and snapshot the active set ATOMICALLY under _syncRoot: a new local turn either
        // registered its completion before this lock (so it is in the snapshot and awaited) or hits admission after and
        // is rejected (RegisterActiveInvocationCompletion returns null). No local turn can slip into the gap between the
        // fence and the snapshot and become an untracked active run.
        Task[] activeInvocationTasks;
        lock (_syncRoot)
        {
            _draining = true;
            activeInvocationTasks = _activeInvocationCompletions.Values.Select(static completion => completion.Task).ToArray();
        }

        if (activeInvocationTasks.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(activeInvocationTasks).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Cancel(Guid invocationId)
    {
        CancelCore(invocationId, CancellationOrigin.User);
    }

    public void CancelDetached(Guid invocationId)
    {
        CancelCore(invocationId, CancellationOrigin.DetachedGraceExpired);
    }

    private void CancelCore(Guid invocationId, CancellationOrigin origin)
    {
        CancellationTokenSource? invocationCancellationTokenSource = null;

        lock (_syncRoot)
        {
            if (_currentInvocationId == invocationId)
            {
                invocationCancellationTokenSource = _invocationCancellationTokenSource;
                _requestedCancellationOrigin = origin;
            }
        }

        invocationCancellationTokenSource?.Cancel();
        CancelPendingToolCalls(invocationId);
    }

    public void CancelAll()
    {
        CancellationTokenSource? invocationCancellationTokenSource;

        lock (_syncRoot)
        {
            invocationCancellationTokenSource = _invocationCancellationTokenSource;

            // An external stop of everything in flight (the hub's disconnect request), NOT the invocation watchdog:
            // record it here so the turn is classified as a shutdown-style cancellation rather than a timeout.
            if (invocationCancellationTokenSource is not null && _requestedCancellationOrigin == CancellationOrigin.Unknown)
            {
                _requestedCancellationOrigin = CancellationOrigin.Shutdown;
            }
        }

        invocationCancellationTokenSource?.Cancel();

        foreach (var pendingToolCall in _pendingToolCalls)
        {
            if (_pendingToolCalls.TryRemove(pendingToolCall.Key, out var removedPendingToolCall))
            {
                removedPendingToolCall.ApprovalCompletion.TrySetCanceled(CancellationToken.None);
                removedPendingToolCall.ResultCompletion.TrySetCanceled(CancellationToken.None);
            }
        }
    }

    /// <summary>
    ///     Re-points the whole-turn watchdog — the <c>CancelAfter</c> armed in <see cref="RegisterActiveInvocation" /> —
    ///     at a deadline measured from NOW, so a human round-trip is not charged to the model's turn budget.
    ///     <para>
    ///         Before parking on a human the deadline is pushed past the longest permitted wait; once the human has
    ///         answered it is re-armed to a full, fresh <c>InvocationTimeout</c>. This does NOT make any wait unbounded:
    ///         each wait keeps its own linked <c>CancelAfter(_maxPendingToolCallAge)</c>, which was previously dead code
    ///         because the shorter invocation deadline always fired first. The net effect is that
    ///         <c>MaxPendingToolCallAge</c> (operator-configurable, 10 min by default) becomes the real cap on operator
    ///         thinking time, instead of "whatever the model left over from its InvocationTimeout".
    ///     </para>
    ///     <para>
    ///         The park extension applies only while a client is ATTACHED. A park whose watcher has gone away is
    ///         waiting for an answer that cannot arrive, so it falls back to a plain <c>InvocationTimeout</c> backstop
    ///         and <c>DetachedInvocationReaper</c>'s grace normally ends it first. A run that never attached over the
    ///         hub at all — a scheduled run, a platform-hub run — is NOT detached and keeps today's full park budget.
    ///     </para>
    ///     <para>
    ///         Re-arming under <see cref="_syncRoot" /> is what makes it safe against a concurrent teardown:
    ///         <see cref="ClearActiveInvocation" /> nulls the field under the same lock BEFORE disposing the source, so a
    ///         non-null source observed here cannot already be disposed.
    ///     </para>
    /// </summary>
    public void SetInvocationDeadline(bool parkedOnHuman)
    {
        lock (_syncRoot)
        {
            _parkedOnHuman = parkedOnHuman;
            ApplyInvocationDeadline();
        }
    }

    // Caller must hold _syncRoot.
    private void ApplyInvocationDeadline()
    {
        if (_invocationCancellationTokenSource is not { } invocationCancellationTokenSource)
        {
            return;
        }

        // The parked deadline keeps the model's own budget on top of the human cap purely as a backstop: if the
        // re-arm on release were ever skipped, the turn still gets its normal InvocationTimeout rather than none.
        var extendPark = _parkedOnHuman
                         && _currentInvocationId is { } invocationId
                         && !_attachmentTracker.IsDetached(invocationId);
        invocationCancellationTokenSource.CancelAfter(extendPark ? _maxPendingToolCallAge + _invocationTimeout : _invocationTimeout);
    }

    // A client attaching or detaching mid-park changes which deadline the park is entitled to, and neither park site is
    // running code at that moment — so the re-arm has to come from here. Without it a reload during an approval park
    // would inherit whatever budget the detached park left behind.
    private void OnAttachmentChanged(object? sender, InvocationAttachmentChangedEventArgs args)
    {
        lock (_syncRoot)
        {
            if (_parkedOnHuman && _currentInvocationId == args.InvocationId)
            {
                ApplyInvocationDeadline();
            }
        }
    }

    // Registers the invocation's active-completion source. Returns null when the node is draining and this is a local
    // turn — the completion add and the draining check happen under _syncRoot so they are serialized with
    // the drain snapshot, closing the admission-after-snapshot race. A non-local (remote) turn is not fenced here; the
    // dispatcher already stops accepting remote assignments at drain.
    public TaskCompletionSource? RegisterActiveInvocationCompletion(Guid invocationId, bool isLocalLoopback)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_syncRoot)
        {
            if (_draining && isLocalLoopback)
            {
                return null;
            }

            if (!_activeInvocationCompletions.TryAdd(invocationId, completion))
            {
                throw new InvalidOperationException($"Invocation {invocationId} is already tracked as active.");
            }
        }

        return completion;
    }

    public void CompleteActiveInvocation(Guid invocationId, TaskCompletionSource completion)
    {
        _activeInvocationCompletions.TryRemove(invocationId, out _);
        completion.TrySetResult();
    }

    // Attributes the cancellation that ended the turn, from state that is already observable when the failure is
    // mapped: a deliberate cancel recorded by its own requester, the linked caller token, or — by elimination — the
    // invocation source's CancelAfter watchdog. Deliberately does NOT consult a flag set from a token callback:
    // callbacks run in reverse registration order, so a callback the runner registers at invocation registration is
    // invoked AFTER every later registration (the streaming agent's own), and the released agent can reach this
    // mapping before it ever ran — which reported a genuine watchdog timeout as a plain cancellation.
    // Resolved ONCE per cancelled turn so the failure category and the metric category cannot disagree.
    public CancellationOrigin ResolveCancellationOrigin()
    {
        lock (_syncRoot)
        {
            if (_requestedCancellationOrigin != CancellationOrigin.Unknown)
            {
                return _requestedCancellationOrigin;
            }

            if (_hostCancellationToken.IsCancellationRequested)
            {
                return CancellationOrigin.Shutdown;
            }

            // Nobody asked and the caller's token is still live, so a cancelled invocation source can only be its own
            // CancelAfter watchdog. With NOTHING of ours cancelled the OperationCanceledException came from below us —
            // an HTTP client timeout surfaces as a TaskCanceledException on a token nobody here owns (the same shape
            // ProviderStreamResilience.IsTransient treats as a provider timeout). Calling that "stopped externally" was
            // wrong twice over: it blamed a shutdown/disconnect that never happened and hid a real timeout behind the
            // Cancelled category.
            return _invocationCancellationTokenSource?.IsCancellationRequested == true
                ? CancellationOrigin.Watchdog
                : CancellationOrigin.ProviderTimeout;
        }
    }

    public static FailureCategory ClassifyCancellation(CancellationOrigin origin)
    {
        return origin is CancellationOrigin.Watchdog or CancellationOrigin.ProviderTimeout
            ? FailureCategory.Timeout
            : FailureCategory.Cancelled;
    }

    /// <summary>
    ///     The fixed, path-free sentence surfaced (and persisted) for a cancelled turn, naming WHICH bound ended it.
    ///     <see cref="FailureCategory" /> alone cannot carry this: it collapses the invocation watchdog, the stream-idle
    ///     watchdog and an HTTP timeout into one <see cref="FailureCategory.Timeout" /> value, and adding a category
    ///     would drift the generated OpenAPI/zod client — so the message is the breadcrumb channel, exactly as it already
    ///     is for <c>StreamIdleTimeoutException</c> (whose own message names the stream-idle bound and its seconds).
    ///     Only the resolved origin and the turn's own configured ceiling are interpolated; nothing here can carry a
    ///     host, path, or model name.
    /// </summary>
    public static string DescribeCancellation(CancellationOrigin origin, TimeSpan invocationTimeout)
    {
        return origin switch
        {
            CancellationOrigin.User => "Stopped by user.",
            CancellationOrigin.Watchdog =>
                $"Timed out: the response exceeded the node maximum message request timeout ({invocationTimeout.TotalSeconds:0}s).",
            CancellationOrigin.DetachedGraceExpired =>
                "Stopped: no client was attached to this run and the disconnect grace period expired.",
            CancellationOrigin.ProviderTimeout =>
                "Timed out: the model provider stopped responding before the node's own ceiling was reached.",
            // Shutdown (and the unreachable Unknown): the host token, the caller's token, or a disconnect-driven
            // CancelAll. The metric collapses all three under "shutdown" too, so the sentence names both plausible
            // causes rather than asserting a shutdown that may not have happened.
            _ => "Stopped externally (node shutdown or client disconnect)."
        };
    }

    // The cancellation cause for the invocation_cancelled_total metric: an explicit user cancel, the
    // invocation-level timeout firing ("watchdog"), or an external cancellation — the caller/host token or a
    // disconnect-driven CancelAll — reported as "shutdown".
    public static string ClassifyCancellationMetricCategory(CancellationOrigin origin)
    {
        return origin switch
        {
            CancellationOrigin.User => "user",
            CancellationOrigin.Watchdog => "watchdog",
            CancellationOrigin.DetachedGraceExpired => "detached_grace",
            CancellationOrigin.ProviderTimeout => "provider_timeout",
            _ => "shutdown"
        };
    }

    public void CancelPendingToolCalls(Guid invocationId)
    {
        foreach (var pendingToolCall in _pendingToolCalls)
        {
            if (pendingToolCall.Value.InvocationId != invocationId)
            {
                continue;
            }

            if (_pendingToolCalls.TryRemove(pendingToolCall.Key, out var removedPendingToolCall))
            {
                removedPendingToolCall.ApprovalCompletion.TrySetCanceled(CancellationToken.None);
                removedPendingToolCall.ResultCompletion.TrySetCanceled(CancellationToken.None);
            }
        }
    }

    public void RegisterActiveInvocation(Guid invocationId, TimeSpan invocationTimeout, CancellationToken cancellationToken)
    {
        CancellationTokenSource? invocationCancellationTokenSource = null;

        try
        {
            invocationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            invocationCancellationTokenSource.CancelAfter(invocationTimeout);

            lock (_syncRoot)
            {
                if (_currentInvocationId is not null)
                {
                    throw new InvalidOperationException("Worker is busy with another invocation");
                }

                _currentInvocationId = invocationId;
                _requestedCancellationOrigin = CancellationOrigin.Unknown;
                _hostCancellationToken = cancellationToken;
                _invocationCancellationTokenSource = invocationCancellationTokenSource;

                // Retained so a human round-trip can re-arm this same deadline (see SetInvocationDeadline).
                _invocationTimeout = invocationTimeout;
                invocationCancellationTokenSource = null;
            }
        }
        finally
        {
            invocationCancellationTokenSource?.Dispose();
        }
    }

    // Internal, not public: only InvocationRunner (same assembly) reads the active turn's token, and a public
    // getter would trip CA1024's "use a property" rule for a member that legitimately throws when no turn is active.
    internal CancellationToken GetInvocationCancellationToken()
    {
        lock (_syncRoot)
        {
            if (_invocationCancellationTokenSource is null)
            {
                throw new InvalidOperationException("No active invocation is registered.");
            }

            return _invocationCancellationTokenSource.Token;
        }
    }

    public bool IsCurrentInvocation(Guid invocationId)
    {
        lock (_syncRoot)
        {
            return _currentInvocationId == invocationId;
        }
    }

    public void ClearActiveInvocation(Guid invocationId)
    {
        CancellationTokenSource? invocationCancellationTokenSource;

        lock (_syncRoot)
        {
            if (_currentInvocationId != invocationId)
            {
                return;
            }

            invocationCancellationTokenSource = _invocationCancellationTokenSource;
            _invocationCancellationTokenSource = null;
            _invocationTimeout = TimeSpan.Zero;
            _parkedOnHuman = false;
            _currentInvocationId = null;
            _requestedCancellationOrigin = CancellationOrigin.Unknown;
            _hostCancellationToken = CancellationToken.None;
        }

        invocationCancellationTokenSource?.Dispose();
    }

    /// <summary>
    ///     What ended a cancelled invocation. <see cref="Unknown" /> is the resting value: no deliberate cancel was
    ///     requested, so the origin is derived from the caller token and the invocation source in
    ///     <see cref="ResolveCancellationOrigin" />.
    /// </summary>
    public enum CancellationOrigin
    {
        Unknown = 0,
        User = 1,
        Watchdog = 2,
        Shutdown = 3,

        /// <summary>
        ///     The disconnect grace elapsed with no client attached (<c>DetachedInvocationReaper</c>). Classified as a
        ///     plain cancellation like a user stop — the turn was abandoned, not timed out — but kept distinct so the
        ///     logs and the cancellation metric can tell an abandoned turn from one the operator stopped.
        /// </summary>
        DetachedGraceExpired = 4,

        /// <summary>
        ///     No token of ours fired: the cancellation came from below the runner, which in practice is the provider's
        ///     own HTTP timeout (a <see cref="TaskCanceledException" /> on a token this node does not own). Classified
        ///     as a <see cref="FailureCategory.Timeout" />, not a cancellation — nothing stopped this turn on purpose.
        /// </summary>
        ProviderTimeout = 5
    }
}
