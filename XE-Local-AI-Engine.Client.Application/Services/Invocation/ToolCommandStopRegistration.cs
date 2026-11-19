namespace XE_Local_AI_Engine.Client.Services.Invocation;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>One running command in the <see cref="ToolCommandStopRegistry" />; disposing removes it unless a later command replaced it.</summary>
public sealed class ToolCommandStopRegistration : IDisposable
{
    private readonly Action<ToolCommandStopRegistration> _remove;
    private int _pendingStops;
    private volatile bool _stopped;

    internal ToolCommandStopRegistration(string key, ISandboxRuntimeProvider provider, SandboxHandle handle, string executionId, Action<ToolCommandStopRegistration> remove)
    {
        Key = key;
        Provider = provider;
        Handle = handle;
        ExecutionId = executionId;
        _remove = remove;
    }

    /// <summary>The operator asked to stop this command, so a not-completed result is a stop, not a timeout.</summary>
    /// <remarks>
    ///     True while a stop is in flight or after one succeeded, so a failed stop never clears a concurrent successful one. The
    ///     count is read first: <see cref="EndStop" /> sets the flag before it decrements, so a zero count read here means a flag
    ///     already set by every stop that succeeded.
    /// </remarks>
    public bool StopRequested => Volatile.Read(ref _pendingStops) > 0 || _stopped;

    internal string Key { get; }

    internal ISandboxRuntimeProvider Provider { get; }

    internal SandboxHandle Handle { get; }

    internal string ExecutionId { get; }

    internal void BeginStop() => _ = Interlocked.Increment(ref _pendingStops);

    internal void EndStop(bool stopped)
    {
        // Recorded before the in-flight count drops, so StopRequested never reads false between the two.
        if (stopped)
        {
            _stopped = true;
        }

        _ = Interlocked.Decrement(ref _pendingStops);
    }

    public void Dispose()
    {
        _remove(this);
    }
}
