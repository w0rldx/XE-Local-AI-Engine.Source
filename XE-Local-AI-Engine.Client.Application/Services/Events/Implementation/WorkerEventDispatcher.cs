namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Represents worker event dispatcher.
/// </summary>
[SuppressMessage("Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Registered for the application lifetime; disposing the service provider owns singleton cleanup.")]
public sealed partial class WorkerEventDispatcher : IWorkerEventDispatcher
{
    private readonly IInvocationHistory _invocationHistory;

    // One ACTIVE invocation at a time, node-wide: every turn takes this slot through ReportInvocationAssignedAsync and holds it
    // until its lease is disposed, except while parked on a human (ReleaseSlotForPark, then ReacquireSlotAsync on resume).
    private readonly SemaphoreSlim _invocationQueue = new(initialCount: 1, maxCount: 1);
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<WorkerEventDispatcher> _logger;

    private readonly Lock _syncRoot = new();
    private readonly TimeProvider _timeProvider;

    // Every invocation assigned and not yet terminal, by id, mutated only under _syncRoot. A terminal update publishes, then removes its entry.
    private readonly Dictionary<Guid, InvocationState> _live = new();

    // Turns that reported a terminal while their lease is still out, by id, so a just-ended holder stays resolvable. Guarded by _syncRoot.
    private readonly Dictionary<Guid, InvocationState> _ended = new();

    // The invocation whose lease holds the slot, or null while the slot is free. Guarded by _syncRoot.
    private Guid? _slotHolderId;

    public WorkerEventDispatcher(IInvocationRunner invocationRunner,
        IInvocationHistory invocationHistory,
        ILogger<WorkerEventDispatcher> logger,
        TimeProvider timeProvider)
    {
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _invocationHistory = invocationHistory ?? throw new ArgumentNullException(nameof(invocationHistory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    public event EventHandler<ToolCallLifecycleChangedEventArgs>? ToolCallLifecycleChanged;

    public event EventHandler<TurnNoticeChangedEventArgs>? TurnNoticeChanged;

    public event EventHandler<ApprovalRequestedChangedEventArgs>? ApprovalRequestedChanged;

    public event EventHandler<UserQuestionRequestedChangedEventArgs>? UserQuestionRequestedChanged;

    // The last assigned invocation, mutated in place only under _syncRoot; an off-lock read is memory-safe but may observe a transient value mid-append
    // (see IWorkerEventDispatcher.CurrentInvocation). Internal callers hold _syncRoot; SlotHolder and GetLiveInvocations return locked clones.
    public InvocationState? CurrentInvocation { get; private set; }

    public InvocationState? SlotHolder
    {
        get
        {
            lock (_syncRoot)
            {
                return _slotHolderId is { } holderId ? FindState(holderId)?.Clone() : null;
            }
        }
    }

    public IReadOnlyList<InvocationState> GetLiveInvocations()
    {
        lock (_syncRoot)
        {
            return [.. _live.Values.Select(static state => state.Clone())];
        }
    }

    public InvocationLiveSnapshot GetLiveSnapshot()
    {
        lock (_syncRoot)
        {
            var current = _slotHolderId is { } holderId ? FindState(holderId) : null;
            if (current is null && CurrentInvocation is { } last && !_live.ContainsKey(last.InvocationId))
            {
                current = last;
            }

            return new InvocationLiveSnapshot
            {
                Current = current?.Clone(),
                Live = [.. _live.Values.Select(static state => state.Clone())]
            };
        }
    }

    /// <summary>TEST-ONLY: clears <see cref="CurrentInvocation" /> back to null under the dispatcher's lock.</summary>
    /// <remarks>
    ///     Production never resets the slot (it is only ever assigned), so e2e tests that share a single
    ///     <see cref="WorkerEventDispatcher" /> via <c>PerTestSession</c> use this to stop a completed chat's invocation
    ///     from leaking into the Invocations empty-state assertions. Exposed to the e2e test assembly via
    ///     <c>InternalsVisibleTo</c>; not part of the public contract. The slot holder is kept: a lease still held from an earlier test must free its permit.
    /// </remarks>
    internal void ResetForTests()
    {
        lock (_syncRoot)
        {
            CurrentInvocation = null;
            _live.Clear();
            _ended.Clear();
        }
    }
}
