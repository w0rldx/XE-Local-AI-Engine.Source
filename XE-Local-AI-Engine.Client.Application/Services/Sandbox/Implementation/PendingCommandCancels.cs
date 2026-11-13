namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     Execution ids a <see cref="ISandboxRuntimeProvider.CancelCommandAsync" /> named before their command registered as in flight, so
///     a stop that lands during a slow start (bwrap, MXC, a Docker exec) cancels the command the moment it registers.
/// </summary>
/// <remarks>
///     Bounded and short-lived: a latch for an id that never starts (it already finished, or never will) expires after
///     <see cref="Lifetime" />, and past <see cref="Capacity" /> the oldest is dropped. Not thread-safe: the provider checks the
///     in-flight set and latches under the same lock it registers under, so a cancel and a start cannot slip past each other.
/// </remarks>
internal sealed class PendingCommandCancels
{
    /// <summary>How long a latched cancel waits for its command to register.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    internal const int Capacity = 16;

    private readonly Dictionary<string, long> _latchedAt = new(StringComparer.Ordinal);

    /// <summary>Remembers a cancel for <paramref name="executionId" /> until it registers or expires.</summary>
    public void Latch(string executionId, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        ArgumentNullException.ThrowIfNull(timeProvider);

        foreach (var expired in _latchedAt.Where(entry => timeProvider.GetElapsedTime(entry.Value) > Lifetime).Select(static entry => entry.Key).ToList())
        {
            _ = _latchedAt.Remove(expired);
        }

        if (!_latchedAt.ContainsKey(executionId) && _latchedAt.Count >= Capacity)
        {
            _ = _latchedAt.Remove(_latchedAt.MinBy(static entry => entry.Value).Key);
        }

        _latchedAt[executionId] = timeProvider.GetTimestamp();
    }

    /// <summary>True, once, when a cancel for <paramref name="executionId" /> was latched and has not expired.</summary>
    public bool TryConsume(string executionId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return _latchedAt.Remove(executionId, out var latchedAt) && timeProvider.GetElapsedTime(latchedAt) <= Lifetime;
    }
}
