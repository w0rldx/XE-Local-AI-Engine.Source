namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;

[SuppressMessage("Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Registered for the application lifetime; disposing the service provider owns singleton cleanup.")]
/// <summary>
///     Represents worker event dispatcher.
/// </summary>
public sealed partial class WorkerEventDispatcher : IWorkerEventDispatcher
{
    private const string AadMismatchReason = "aad-mismatch";
    private const string RetiredKeyReason = "retired-key";

    private readonly Lazy<IHubMessageSender> _hubMessageSender;
    private readonly IInvocationHistory _invocationHistory;
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<WorkerEventDispatcher> _logger;
    private readonly INodeKeyRegistry _nodeKeyRegistry;
    private readonly SemaphoreSlim _remoteInvocationQueue = new(initialCount: 1, maxCount: 1);
    private readonly INodeChatRemotePersistenceCoordinator _remotePersistenceCoordinator;
    private readonly IRuntimePackageEnvelopeAssembler _runtimePackageEnvelopeAssembler;

    // Cancelled when the worker stops accepting remote invocations (drain), so a remote assignment still
    // BLOCKED on the invocation slot is abandoned instead of waiting forever — the previously uncancelable
    // `_remoteInvocationQueue.WaitAsync()` on the two remote paths could hang a draining node indefinitely. A running
    // invocation (past the wait) is unaffected: it runs under its own token, not this one.
    [SuppressMessage("Sonar",
        "S2930:\"IDisposables\" should be \"Dispose\"d",
        Justification =
            "App-lifetime singleton (see the CA1001 suppression on the type). It is Cancel()-only — no CancelAfter timer and its WaitHandle is never accessed — so it holds no unmanaged resource to reclaim before process exit.")]
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly Lock _syncRoot = new();
    private bool _isAcceptingRemoteInvocations = true;

    public WorkerEventDispatcher(IInvocationRunner invocationRunner,
        IRuntimePackageEnvelopeAssembler runtimePackageEnvelopeAssembler,
        Lazy<IHubMessageSender> hubMessageSender,
        INodeKeyRegistry nodeKeyRegistry,
        IInvocationHistory invocationHistory,
        INodeChatRemotePersistenceCoordinator remotePersistenceCoordinator,
        ILogger<WorkerEventDispatcher> logger)
    {
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _runtimePackageEnvelopeAssembler = runtimePackageEnvelopeAssembler ?? throw new ArgumentNullException(nameof(runtimePackageEnvelopeAssembler));
        _hubMessageSender = hubMessageSender ?? throw new ArgumentNullException(nameof(hubMessageSender));
        _nodeKeyRegistry = nodeKeyRegistry ?? throw new ArgumentNullException(nameof(nodeKeyRegistry));
        _invocationHistory = invocationHistory ?? throw new ArgumentNullException(nameof(invocationHistory));
        _remotePersistenceCoordinator = remotePersistenceCoordinator ?? throw new ArgumentNullException(nameof(remotePersistenceCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    public event EventHandler<ToolCallLifecycleChangedEventArgs>? ToolCallLifecycleChanged;

    public event EventHandler<TurnNoticeChangedEventArgs>? TurnNoticeChanged;

    public event EventHandler<ApprovalRequestedChangedEventArgs>? ApprovalRequestedChanged;

    public event EventHandler<UserQuestionRequestedChangedEventArgs>? UserQuestionRequestedChanged;

    // The live invocation, mutated in place only under _syncRoot. Its StreamedContent/StreamedThinkingContent now
    // materialize from an immutable append-only accumulator, so an off-lock read is memory-safe (though it may observe a
    // transient value mid-append) — see IWorkerEventDispatcher.CurrentInvocation. Internal callers already hold _syncRoot
    // when they touch it; GetCurrentInvocationSnapshot returns a locked clone for anyone who needs a consistent copy.
    public InvocationState? CurrentInvocation { get; private set; }

    public bool IsAcceptingRemoteInvocations
    {
        get
        {
            lock (_syncRoot)
            {
                return _isAcceptingRemoteInvocations;
            }
        }
    }

    public void StopAcceptingRemoteInvocations()
    {
        lock (_syncRoot)
        {
            if (!_isAcceptingRemoteInvocations)
            {
                return;
            }

            _isAcceptingRemoteInvocations = false;
        }

        // Release any remote assignment still WAITING for the slot: at drain it must not start (it has not yet acquired
        // the slot). Fired once (guarded by the flag transition above). Cancelled outside the lock so continuations do
        // not run under it.
        _shutdownCts.Cancel();
        _logger.LogInformation("WorkerEventDispatcher stopped accepting new remote invocation assignments for shutdown drain.");
    }

    /// <summary>
    ///     TEST-ONLY: clears <see cref="CurrentInvocation" /> back to null under the dispatcher's lock.
    ///     Production never resets the slot (it is only ever assigned), so e2e tests that share a single
    ///     <see cref="WorkerEventDispatcher" /> via <c>PerTestSession</c> use this to stop a completed
    ///     chat's invocation from leaking into the Invocations empty-state assertions. Exposed to the e2e
    ///     test assembly via <c>InternalsVisibleTo</c>; not part of the public contract.
    /// </summary>
    internal void ResetForTests()
    {
        lock (_syncRoot)
        {
            CurrentInvocation = null;
        }
    }
}
