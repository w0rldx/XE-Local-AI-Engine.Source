namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Options;

/// <summary>An <see cref="IOptionsMonitor{TOptions}" /> over a fixed value.</summary>
internal sealed class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
{
    public StaticOptionsMonitor(TOptions value)
    {
        CurrentValue = value;
    }

    public TOptions CurrentValue { get; }

    public TOptions Get(string? name)
    {
        return CurrentValue;
    }

    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        return null;
    }
}

/// <summary>A <see cref="TimeProvider" /> pinned to one instant, so timestamps in assertions are exact.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }
}
