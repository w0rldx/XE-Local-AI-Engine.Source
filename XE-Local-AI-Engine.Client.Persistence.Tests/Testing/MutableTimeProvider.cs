namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>A <see cref="TimeProvider" /> whose clock moves only when a test advances it, in Unix milliseconds.</summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private long _milliseconds;

    public MutableTimeProvider(long initialMilliseconds)
    {
        _milliseconds = initialMilliseconds;
    }

    public void Advance(long milliseconds)
    {
        _milliseconds += milliseconds;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(_milliseconds);
    }
}
