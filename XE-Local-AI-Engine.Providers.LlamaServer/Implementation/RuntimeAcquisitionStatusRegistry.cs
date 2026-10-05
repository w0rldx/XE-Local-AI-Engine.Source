namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IRuntimeAcquisitionStatusRegistry" />: a single sequenced snapshot behind a lock, plus a
///     throttled fire-and-forget broadcast through <see cref="IRuntimeAcquisitionEventPublisher" />.
/// </summary>
/// <remarks>
///     THROTTLE RULE: only repeated byte updates within one
///     (<see cref="RuntimeAcquisitionUpdate.Phase" />, <see cref="RuntimeAcquisitionUpdate.StepIndex" />) pair are
///     throttled, while any phase change, any step change and every terminal status pushes immediately. It is
///     deliberately NOT a port of <c>GgufDownloadCoordinator.SetStatus</c>, which bypasses the throttle for the initial
///     and terminal pushes only. See docs/wiki/03-local-runtime-and-providers.md, "The acquisition throttle rule".
/// </remarks>
public sealed class RuntimeAcquisitionStatusRegistry : IRuntimeAcquisitionStatusRegistry
{
    /// <summary>Minimum wall-clock gap between two byte-progress pushes within one (phase, step).</summary>
    internal static readonly TimeSpan ProgressPushInterval = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly ILogger<RuntimeAcquisitionStatusRegistry> _logger;
    private readonly IRuntimeAcquisitionEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;

    private RuntimeAcquisitionStatusEvent _current = Empty;
    private long _lastPushTicks;
    private long _sequence;

    public RuntimeAcquisitionStatusRegistry(IRuntimeAcquisitionEventPublisher publisher,
        ILogger<RuntimeAcquisitionStatusRegistry> logger,
        TimeProvider timeProvider)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>The pre-acquisition snapshot: nothing attempted yet in this process lifetime.</summary>
    public static RuntimeAcquisitionStatusEvent Empty { get; } = new()
    {
        Sequence = 0,
        Phase = nameof(RuntimeAcquisitionPhase.Idle),
        Variant = null,
        Tag = null,
        CompletedBytes = null,
        TotalBytes = null,
        StepIndex = 1,
        StepCount = 1,
        SanitizedError = null
    };

    /// <inheritdoc />
    public RuntimeAcquisitionStatusEvent Current
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

        RuntimeAcquisitionStatusEvent? toPush;
        lock (_gate)
        {
            toPush = WriteLocked(update);
        }

        if (toPush is not null)
        {
            _ = PublishAsync(toPush);
        }
    }

    /// <inheritdoc />
    public bool TryClearFailure(string variant, string tag, string reasonPrefix)
    {
        RuntimeAcquisitionStatusEvent? toPush;
        lock (_gate)
        {
            // The check and the write share the lock, so a status another acquisition reported after the caller looked is never overwritten.
            if (!IsFailureOf(_current, variant, tag, reasonPrefix))
            {
                return false;
            }

            toPush = WriteLocked(new RuntimeAcquisitionUpdate
            {
                Phase = RuntimeAcquisitionPhase.Idle,
                Variant = variant,
                Tag = tag,
                StepIndex = 1,
                StepCount = _current.StepCount
            });
        }

        if (toPush is not null)
        {
            _ = PublishAsync(toPush);
        }

        return true;
    }

    /// <summary>The condition <see cref="TryClearFailure" /> clears under: a Failed for this variant and tag whose reason starts with the prefix.</summary>
    internal static bool IsFailureOf(RuntimeAcquisitionStatusEvent status, string variant, string tag, string reasonPrefix)
    {
        return string.Equals(status.Phase, nameof(RuntimeAcquisitionPhase.Failed), StringComparison.Ordinal)
               && string.Equals(status.Variant, variant, StringComparison.Ordinal)
               && string.Equals(status.Tag, tag, StringComparison.Ordinal)
               && status.SanitizedError?.StartsWith(reasonPrefix, StringComparison.Ordinal) == true;
    }

    /// <summary>Records <paramref name="update" /> under <see cref="_gate" />; returns the status to push, or <see langword="null" /> when the throttle holds it.</summary>
    private RuntimeAcquisitionStatusEvent? WriteLocked(RuntimeAcquisitionUpdate update)
    {
        // Byte updates repeat within one (phase, step); a phase or step transition — and every terminal status —
        // must reach connected clients immediately or the UI narrates the wrong stage.
        var isRepeatWithinStep = string.Equals(_current.Phase, update.Phase.ToString(), StringComparison.Ordinal)
                                 && _current.StepIndex == update.StepIndex
                                 && !IsTerminal(update.Phase);

        // The write itself is unconditional, so the hydrate endpoint always serves the freshest bytes even while a
        // push is being throttled.
        _current = new RuntimeAcquisitionStatusEvent
        {
            Sequence = ++_sequence,
            Phase = update.Phase.ToString(),
            Variant = update.Variant,
            Tag = update.Tag,
            CompletedBytes = update.CompletedBytes,
            TotalBytes = update.TotalBytes,
            StepIndex = update.StepIndex,
            StepCount = update.StepCount,
            SanitizedError = update.SanitizedError
        };

        var now = _timeProvider.GetUtcNow().UtcTicks;
        if (isRepeatWithinStep && now - _lastPushTicks < ProgressPushInterval.Ticks)
        {
            return null;
        }

        _lastPushTicks = now;
        return _current;
    }

    private static bool IsTerminal(RuntimeAcquisitionPhase phase)
    {
        return phase is RuntimeAcquisitionPhase.Completed or RuntimeAcquisitionPhase.Failed;
    }

    private async Task PublishAsync(RuntimeAcquisitionStatusEvent statusEvent)
    {
        try
        {
            // Fire-and-forget push off the byte loop (see the caller's `_ = PublishAsync(...)`): there is no request
            // token to observe, so the token is not propagated, explicitly.
            await _publisher.PublishStatusAsync(statusEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A push failure must never surface on the startup path or stall the byte loop; the hydrate endpoint still
            // serves the same snapshot.
            _logger.LogDebug(exception, "Could not push the llama.cpp runtime acquisition status; the status endpoint still serves it.");
        }
    }
}
