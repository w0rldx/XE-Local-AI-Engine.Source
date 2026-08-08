namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IRuntimeAcquisitionStatusRegistry" />: a single sequenced snapshot behind a lock, plus a
///     throttled fire-and-forget broadcast through <see cref="IRuntimeAcquisitionEventPublisher" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Throttle rule — deliberately NOT a port of <c>GgufDownloadCoordinator.SetStatus</c>.</b> That rule bypasses
///         the throttle only for the initial and terminal pushes, which is sufficient there because a GGUF download has
///         exactly one non-terminal phase. This lifecycle has several (Downloading → Verifying → Extracting), and
///         porting the GGUF rule literally would swallow every transition that landed inside the throttle interval after
///         a byte update: connected clients would sit on <c>Downloading</c> until completion, which is the precise
///         staleness this channel exists to remove.
///     </para>
///     <para>
///         The rule here is instead: <b>throttle only repeated byte updates within the same
///         (<see cref="RuntimeAcquisitionUpdate.Phase" />, <see cref="RuntimeAcquisitionUpdate.StepIndex" />)</b>. Any
///         phase change, any step change, and every terminal status pushes immediately. Throttling is still mandatory
///         for the byte case: the download loop uses an 81 920-byte buffer, so an unthrottled callback fires roughly
///         every 80 KB and would flood the socket.
///     </para>
///     <para>
///         <b>Non-blocking.</b> <see cref="Report" /> is called from the download byte loop and from the startup path.
///         The publish is fire-and-forget with its failure swallowed to a debug log, exactly as
///         <c>GgufDownloadCoordinator.BroadcastStatus</c> does — the hydrate endpoint remains authoritative either way.
///     </para>
/// </remarks>
public sealed class RuntimeAcquisitionStatusRegistry : IRuntimeAcquisitionStatusRegistry
{
    /// <summary>Minimum wall-clock gap between two byte-progress pushes within one (phase, step).</summary>
    internal static readonly TimeSpan ProgressPushInterval = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly ILogger<RuntimeAcquisitionStatusRegistry> _logger;
    private readonly IRuntimeAcquisitionEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;

    private RuntimeAcquisitionStatusHubEvent _current = Empty;
    private long _lastPushTicks;
    private long _sequence;

    public RuntimeAcquisitionStatusRegistry(IRuntimeAcquisitionEventPublisher publisher,
        ILogger<RuntimeAcquisitionStatusRegistry> logger,
        TimeProvider? timeProvider = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The pre-acquisition snapshot: nothing attempted yet in this process lifetime.</summary>
    public static RuntimeAcquisitionStatusHubEvent Empty { get; } = new(Sequence: 0,
        nameof(RuntimeAcquisitionPhase.Idle),
        Variant: null,
        Tag: null,
        CompletedBytes: null,
        TotalBytes: null,
        StepIndex: 1,
        StepCount: 1,
        SanitizedError: null);

    /// <inheritdoc />
    public RuntimeAcquisitionStatusHubEvent Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public void Report(RuntimeAcquisitionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        RuntimeAcquisitionStatusHubEvent? toPush;
        lock (_gate)
        {
            // Byte updates repeat within one (phase, step); a phase or step transition — and every terminal status —
            // must reach connected clients immediately or the UI narrates the wrong stage.
            var isRepeatWithinStep = string.Equals(_current.Phase, update.Phase.ToString(), StringComparison.Ordinal)
                                     && _current.StepIndex == update.StepIndex
                                     && !IsTerminal(update.Phase);

            // The write itself is unconditional, so the hydrate endpoint always serves the freshest bytes even while a
            // push is being throttled.
            _current = new RuntimeAcquisitionStatusHubEvent(++_sequence,
                update.Phase.ToString(),
                update.Variant,
                update.Tag,
                update.CompletedBytes,
                update.TotalBytes,
                update.StepIndex,
                update.StepCount,
                update.SanitizedError);

            var now = _timeProvider.GetUtcNow().UtcTicks;
            if (isRepeatWithinStep && now - _lastPushTicks < ProgressPushInterval.Ticks)
            {
                return;
            }

            _lastPushTicks = now;
            toPush = _current;
        }

        _ = PublishAsync(toPush);
    }

    private static bool IsTerminal(RuntimeAcquisitionPhase phase)
    {
        return phase is RuntimeAcquisitionPhase.Completed or RuntimeAcquisitionPhase.Failed;
    }

    private async Task PublishAsync(RuntimeAcquisitionStatusHubEvent statusEvent)
    {
        try
        {
            await _publisher.PublishStatusAsync(statusEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A push failure must never surface on the startup path or stall the byte loop; the hydrate endpoint still
            // serves the same snapshot.
            _logger.LogDebug(exception, "Could not push the llama.cpp runtime acquisition status; the status endpoint still serves it.");
        }
    }
}
