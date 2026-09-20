namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.Invocation;

[SuppressMessage("Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Registered for the application lifetime; disposing the service provider owns singleton cleanup.")]
/// <summary>
///     Represents worker event dispatcher.
/// </summary>
public sealed partial class WorkerEventDispatcher : IWorkerEventDispatcher
{
    private readonly IInvocationHistory _invocationHistory;

    // One invocation at a time, node-wide: every turn (chat, scheduler, benchmark, integration, workflow) takes this
    // slot through ReportInvocationAssignedAsync and holds it until its lease is disposed.
    private readonly SemaphoreSlim _invocationQueue = new(initialCount: 1, maxCount: 1);
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<WorkerEventDispatcher> _logger;

    private readonly Lock _syncRoot = new();
    private readonly TimeProvider _timeProvider;

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

    // The live invocation, mutated in place only under _syncRoot; an off-lock read is memory-safe but may observe a transient value mid-append
    // (see IWorkerEventDispatcher.CurrentInvocation). Internal callers hold _syncRoot; GetCurrentInvocationSnapshot returns a locked clone.
    public InvocationState? CurrentInvocation { get; private set; }

    /// <summary>TEST-ONLY: clears <see cref="CurrentInvocation" /> back to null under the dispatcher's lock.</summary>
    /// <remarks>
    ///     Production never resets the slot (it is only ever assigned), so e2e tests that share a single
    ///     <see cref="WorkerEventDispatcher" /> via <c>PerTestSession</c> use this to stop a completed chat's invocation
    ///     from leaking into the Invocations empty-state assertions. Exposed to the e2e test assembly via
    ///     <c>InternalsVisibleTo</c>; not part of the public contract.
    /// </remarks>
    internal void ResetForTests()
    {
        lock (_syncRoot)
        {
            CurrentInvocation = null;
        }
    }
}
