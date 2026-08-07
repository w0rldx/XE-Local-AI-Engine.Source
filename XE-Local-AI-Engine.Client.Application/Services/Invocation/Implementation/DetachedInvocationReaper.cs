namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Cancels a run whose last event consumer went away and never came back. Without it, a browser that disconnects
///     while an approval card is on screen leaves the turn parked for
///     <c>MaxPendingToolCallAge + InvocationTimeout</c> (~15 minutes) PER PARK, holding the llama-server collision-slot
///     lease the whole time, waiting for an answer that can no longer arrive.
///     <para>
///         Expiry does nothing special: it cancels the invocation, and the existing machinery unwinds it —
///         <c>RunInvocationAsync</c> maps the <see cref="OperationCanceledException" /> to a Cancelled failure, the pump
///         terminalizes the row, and the <c>finally</c> releases the lease. This is a trigger, not a teardown path.
///     </para>
/// </summary>
public sealed class DetachedInvocationReaper : BackgroundService
{
    /// <summary>
    ///     How often the grace deadline is evaluated. Fixed rather than configurable: the grace itself is the operator
    ///     knob, and 5 s is negligible against its 300 s default.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly IInvocationAttachmentTracker _attachmentTracker;
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<DetachedInvocationReaper> _logger;

    // Invocations already cancelled by this reaper. An entry survives in the tracker until the run reports a terminal
    // state, which is a tick or two later — and forever if the run ignores its cancellation — so without this the same
    // turn would be re-cancelled and re-logged every 5 s. Keyed on the DETACH INSTANT as well as the id, so a
    // detach → re-attach → detach cycle is a new key and becomes reapable again; pruned against the live set per tick.
    private readonly HashSet<DetachedInvocation> _reaped = [];
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly TimeProvider _timeProvider;

    public DetachedInvocationReaper(IInvocationAttachmentTracker attachmentTracker,
        IInvocationRunner invocationRunner,
        INodeRuntimeSettings runtimeSettings,
        TimeProvider timeProvider,
        ILogger<DetachedInvocationReaper> logger)
    {
        _attachmentTracker = attachmentTracker ?? throw new ArgumentNullException(nameof(attachmentTracker));
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(continueOnCapturedContext: false))
        {
            try
            {
                await ReapAsync(stoppingToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to reap detached invocations; retrying on the next tick.");
            }
        }
    }

    /// <summary>
    ///     Reads the grace EVERY tick and never caches it in a field. Capturing a stored node setting in a singleton is
    ///     precisely what silently required a node restart before an operator edit took effect (F-001/F-025); the read
    ///     is an <c>IMemoryCache</c> hit through <c>CachedNodeSettingsStore</c>, so per-tick costs nothing.
    ///     <para>
    ///         One tick's work, <c>internal</c> so tests can drive it directly: the repo's fake clocks override only
    ///         <c>GetUtcNow</c>, so a <see cref="PeriodicTimer" /> built on one still ticks on real time and a
    ///         cadence-driven test would have to sleep for whole ticks.
    ///     </para>
    /// </summary>
    internal async Task ReapAsync(CancellationToken cancellationToken)
    {
        var graceSeconds = await _runtimeSettings.GetDetachedGraceSecondsAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        // 0 disables reaping entirely (today's behavior: a detached run is bounded only by the whole-invocation
        // watchdog). Checked per tick, so flipping it back to a positive value takes effect on the next tick too.
        if (graceSeconds <= 0)
        {
            return;
        }

        var grace = TimeSpan.FromSeconds(graceSeconds);
        var nowUtc = _timeProvider.GetUtcNow();
        var detachedInvocations = _attachmentTracker.ListDetached();
        _reaped.IntersectWith(detachedInvocations);

        foreach (var detached in detachedInvocations)
        {
            if (nowUtc - detached.DetachedAtUtc < grace || !_reaped.Add(detached))
            {
                continue;
            }

            _logger.LogInformation("Cancelling invocation {InvocationId}: no client has been attached for {DetachedSeconds:F0}s, past the {GraceSeconds}s disconnect grace.",
                detached.InvocationId,
                (nowUtc - detached.DetachedAtUtc).TotalSeconds,
                graceSeconds);
            NodeMetrics.ChatDetachedInvocationReapedTotal.Add(1);
            _invocationRunner.CancelDetached(detached.InvocationId);
        }
    }
}
