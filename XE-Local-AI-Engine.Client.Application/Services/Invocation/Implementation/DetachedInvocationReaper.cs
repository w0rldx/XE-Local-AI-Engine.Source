namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>Cancels a run whose last event consumer went away and never came back.</summary>
/// <remarks>
///     Without it, a browser disconnecting while an approval card is on screen leaves the turn parked for
///     <c>MaxPendingToolCallAge + InvocationTimeout</c> PER PARK, holding the llama-server collision-slot lease the
///     whole time for an answer that can no longer arrive. Expiry does nothing special: it cancels the invocation and
///     the existing machinery unwinds it — the failure mapping records Cancelled, the pump terminalizes the row, and
///     the <c>finally</c> releases the lease. A trigger, not a teardown path.
/// </remarks>
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

    // Invocations already cancelled by this reaper. An entry survives in the tracker until the run reports a terminal state — a tick or two later, or never if
    // the run ignores its cancellation — so without this the same turn is re-cancelled every tick. Keyed on the DETACH INSTANT too, so a re-detach is reapable.
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

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReapAsync(stoppingToken);
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

    /// <summary>One tick's work, reading the grace every tick and never caching it in a field.</summary>
    /// <remarks>
    ///     Capturing a stored node setting in a singleton is what makes an operator edit silently require a restart,
    ///     and the read is an <c>IMemoryCache</c> hit through <c>CachedNodeSettingsStore</c>, so a per-tick read costs
    ///     nothing. <c>internal</c> so tests can drive it directly: the repo's fake clocks override only
    ///     <c>GetUtcNow</c>, so a <see cref="PeriodicTimer" /> built on one still ticks on real time and a
    ///     cadence-driven test would have to sleep for whole ticks.
    /// </remarks>
    internal async Task ReapAsync(CancellationToken cancellationToken)
    {
        var graceSeconds = await _runtimeSettings.GetDetachedGraceSecondsAsync(cancellationToken);

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
