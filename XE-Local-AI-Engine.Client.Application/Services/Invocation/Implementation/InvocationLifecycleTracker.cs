namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The active invocation's lifecycle state machine: registration and completion tracking, the whole-turn watchdog
///     and its human-park re-arm, deliberate cancellation, shutdown drain, and cancellation attribution.
/// </summary>
/// <remarks>
///     Everything here is guarded by ONE lock (<see cref="_syncRoot" />), which is the point of the type: the cancellation origin is derived from the same
///     synchronized fields the cancel requesters write, in one acquisition, never from a <c>CancellationToken.Register</c> callback. Callbacks run in
///     reverse registration order, so one registered at invocation registration runs after every later one and the released agent can reach the failure
///     mapping first, reporting a watchdog timeout as a plain cancellation. Two locks, or an earlier snapshot into locals, reintroduce that bug.
/// </remarks>
// Not interfaced: one implementation, and the state is the runner's own. Public only because InvocationRunner's constructor is, and DI activation requires it.
// A singleton for the runner's own reason — it holds the live turn while cancels and results arrive on other call stacks — sharing the one PendingToolCallRegistry.
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

    // Set once (never reset) when shutdown drain begins, guarded by _syncRoot. A local invocation reaching admission after it is set registers
    // AFTER the drain snapshot, so it is rejected rather than becoming an untracked active run the drain never waits for.
    private bool _draining;

    // The caller/host token the active invocation's source is linked to (RegisterActiveInvocation), captured so a cancellation is attributed to the caller
    // rather than the invocation watchdog WITHOUT a token callback: those run in reverse registration order, so the streaming agent's is released first.
    private CancellationToken _hostCancellationToken;

    private CancellationTokenSource? _invocationCancellationTokenSource;

    // The active turn's whole-turn budget, retained so the deadline can be RE-ARMED around a human round-trip
    // (see SetInvocationDeadline). Written and read only under _syncRoot, alongside the source it arms.
    private TimeSpan _invocationTimeout;

    // Whether the active turn is parked waiting on a human (a tool approval or an ask_user question), written and read only under _syncRoot. It exists so the
    // AttachmentChanged handler can re-apply the deadline for a park it did not start: a client re-attaching mid-park gets the full budget from that moment.
    private bool _parkedOnHuman;

    // Why the active invocation was DELIBERATELY cancelled, recorded synchronously under _syncRoot by the requester itself (Cancel / CancelAll). Unknown means
    // nobody asked, so the cancellation came from the invocation's own CancelAfter watchdog or the linked caller token, both read off state at mapping time.
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
        // Fence local admission and snapshot the active set ATOMICALLY under _syncRoot: a new local turn either registered its completion before this lock, so
        // it is in the snapshot and awaited, or hits admission after and is rejected. None can slip into the gap and become an untracked active run.
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
            await Task.WhenAll(activeInvocationTasks).WaitAsync(timeout, cancellationToken);
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

#pragma warning disable MA0045 // Backs IInvocationRunner.Cancel, a synchronous void invoked from the chat cancellation registry's Action callback.
        invocationCancellationTokenSource?.Cancel();
#pragma warning restore MA0045
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
            }
        }
    }

    /// <summary>
    ///     Re-points the whole-turn watchdog at a deadline measured from NOW, so a human round-trip is not charged to
    ///     the model's turn budget.
    /// </summary>
    /// <remarks>
    ///     Before a park the deadline is pushed past the longest permitted wait and re-armed to a fresh <c>InvocationTimeout</c> once answered. No wait becomes
    ///     unbounded — each keeps its own linked <c>CancelAfter(_maxPendingToolCallAge)</c> — so that value, not whatever the model left over, caps operator
    ///     thinking time. The extension applies only while a client is ATTACHED: a park whose watcher left awaits an answer that cannot arrive and falls back
    ///     to a plain backstop the reaper usually ends first, while a run that never attached keeps the full budget. Re-arming under the lock is teardown-safe.
    /// </remarks>
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

    // A client attaching or detaching mid-park changes which deadline the park is entitled to, and neither park site is running code at that moment, so the
    // re-arm has to come from here. Without it a reload during an approval park inherits whatever budget the detached park left behind.
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

    // Registers the invocation's active-completion source, returning null when the node is draining and this is a local turn. The completion add and the
    // draining check happen under _syncRoot, so they serialize with the drain snapshot and close the admission-after-snapshot race.
    public TaskCompletionSource? RegisterActiveInvocationCompletion(Guid invocationId)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_syncRoot)
        {
            if (_draining)
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

    // Attributes the cancellation that ended the turn from state already observable at mapping time: a deliberate cancel recorded by its own requester, the
    // linked caller token, or the CancelAfter watchdog by elimination — never a token-callback flag, which reverse ordering can leave unrun. Resolved ONCE per turn.
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

            // Nobody asked and the caller's token is still live, so a cancelled invocation source can only be its own CancelAfter watchdog. With NOTHING of ours
            // cancelled the exception came from below us — an HTTP client timeout on a token nobody here owns — which is a provider timeout, not an external stop.
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

    /// <summary>The fixed, path-free sentence surfaced and persisted for a cancelled turn, naming WHICH bound ended it.</summary>
    /// <remarks>
    ///     <see cref="FailureCategory" /> alone cannot carry this: it collapses the invocation watchdog, the stream-idle
    ///     watchdog and an HTTP timeout into one <see cref="FailureCategory.Timeout" /> value, and adding a category
    ///     would drift the generated OpenAPI/zod client, so the message is the breadcrumb channel — as it already is for
    ///     <c>StreamIdleTimeoutException</c>, whose message names the stream-idle bound and its seconds. Only the
    ///     resolved origin and the turn's configured ceiling are interpolated; no host, path or model name can ride it.
    /// </remarks>
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
            // Shutdown, and the unreachable Unknown: the host token, the caller's token, or a disconnect-driven CancelAll. The metric collapses all three
            // under "shutdown" too, so the sentence names both plausible causes rather than asserting a shutdown that may not have happened.
            _ => "Stopped externally (node shutdown or client disconnect)."
        };
    }

    // The cancellation cause for the invocation_cancelled_total metric: an explicit user cancel, the invocation-level timeout firing ("watchdog"),
    // or an external cancellation — the caller/host token or a disconnect-driven CancelAll — reported as "shutdown".
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

        /// <summary>The disconnect grace elapsed with no client attached (<c>DetachedInvocationReaper</c>).</summary>
        /// <remarks>
        ///     Classified as a plain cancellation like a user stop — the turn was abandoned, not timed out — but kept
        ///     distinct so the logs and the cancellation metric can tell an abandoned turn from one the operator stopped.
        /// </remarks>
        DetachedGraceExpired = 4,

        /// <summary>
        ///     No token of ours fired: the cancellation came from below the runner, which in practice is the provider's
        ///     own HTTP timeout (a <see cref="TaskCanceledException" /> on a token this node does not own). Classified
        ///     as a <see cref="FailureCategory.Timeout" />, not a cancellation — nothing stopped this turn on purpose.
        /// </summary>
        ProviderTimeout = 5
    }
}
