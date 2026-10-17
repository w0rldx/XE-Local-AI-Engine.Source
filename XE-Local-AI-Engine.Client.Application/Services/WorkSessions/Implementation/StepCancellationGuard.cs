namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The two clocks that can stop one work-session step: the park clock, armed when the turn asks for an approval
///     or an answer and disarmed as soon as it moves again, and the whole-step deadline.
/// </summary>
/// <remarks>
///     Both stop the step the way the operator's stop button does, through <see cref="INodeChatStreamCancellationRegistry" />, which
///     cancels the runner so the pump persists a real <c>Cancelled</c> terminal. Cancelling the supervisor's own enumeration instead
///     would only stop it watching: the run would continue unbounded. A parked turn gives the invocation slot back, so the park clock
///     bounds how long the session waits, not how long the slot is held; an answered park waiting to retake the slot is left alone.
/// </remarks>
internal sealed class StepCancellationGuard : IDisposable
{
    private readonly NodeChatMessageCorrelation _correlation;
    private readonly ITimer _deadlineTimer;
    private readonly ITimer _parkTimer;
    private readonly INodeChatStreamCancellationRegistry _registry;
    private readonly Func<bool> _parkAnswered;
    private readonly Action _onAnsweredPark;
    private int _deadlineExpired;
    private int _parkExpired;
    private string? _parkedToolName;

    /// <param name="parkAnswered">True when the step's card was answered and the turn only waits for the invocation slot.</param>
    /// <param name="onAnsweredPark">Runs instead of the cancel when the park clock fires on an answered park.</param>
    public StepCancellationGuard(INodeChatStreamCancellationRegistry registry,
        NodeChatMessageCorrelation correlation,
        TimeProvider timeProvider,
        Func<bool> parkAnswered,
        Action onAnsweredPark)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _parkAnswered = parkAnswered ?? throw new ArgumentNullException(nameof(parkAnswered));
        _onAnsweredPark = onAnsweredPark ?? throw new ArgumentNullException(nameof(onAnsweredPark));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _parkTimer = timeProvider.CreateTimer(static state => ((StepCancellationGuard)state!).OnParkExpired(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _deadlineTimer = timeProvider.CreateTimer(static state => ((StepCancellationGuard)state!).OnDeadlineExpired(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public bool ParkExpired => Volatile.Read(ref _parkExpired) != 0;

    public bool DeadlineExpired => Volatile.Read(ref _deadlineExpired) != 0;

    /// <summary>The tool whose prompt was left unanswered, for the open question the supervisor records.</summary>
    public string? ParkedToolName => Volatile.Read(ref _parkedToolName);

    public void ArmPark(TimeSpan budget, string? toolName)
    {
        Volatile.Write(ref _parkedToolName, toolName);
        _ = _parkTimer.Change(budget, Timeout.InfiniteTimeSpan);
    }

    public void DisarmPark() =>
        _ = _parkTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void ArmDeadline(TimeSpan budget) =>
        _ = _deadlineTimer.Change(budget, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _parkTimer.Dispose();
        _deadlineTimer.Dispose();
    }

    private void OnParkExpired()
    {
        // An answered park sends no delta until the turn retakes the slot, so the disarm signals cannot cover it: check at fire time.
        if (_parkAnswered())
        {
            _onAnsweredPark();
            return;
        }

        Volatile.Write(ref _parkExpired, value: 1);
        _ = _registry.TryCancel(_correlation);
    }

    private void OnDeadlineExpired()
    {
        Volatile.Write(ref _deadlineExpired, value: 1);
        _ = _registry.TryCancel(_correlation);
    }
}
