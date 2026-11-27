namespace XE_Local_AI_Engine.Client.Testing.Fakes;

/// <summary>
///     A <see cref="TimeProvider" /> whose every wall-clock read is one second after the last, starting one second
///     after the Unix epoch, so rows created one after another sort in creation order. Timers stay real.
/// </summary>
public sealed class SteppingTimeProvider : TimeProvider
{
    private long _ticks = DateTimeOffset.UnixEpoch.Ticks;

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond), TimeSpan.Zero);
}
