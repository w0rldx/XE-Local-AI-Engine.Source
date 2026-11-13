namespace XE_Local_AI_Engine.Client.Services.Invocation;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>One running command in the <see cref="ToolCommandStopRegistry" />; disposing removes it unless a later command replaced it.</summary>
public sealed class ToolCommandStopRegistration : IDisposable
{
    private readonly Action<ToolCommandStopRegistration> _remove;
    private volatile bool _stopRequested;

    internal ToolCommandStopRegistration(string key, ISandboxRuntimeProvider provider, SandboxHandle handle, string executionId, Action<ToolCommandStopRegistration> remove)
    {
        Key = key;
        Provider = provider;
        Handle = handle;
        ExecutionId = executionId;
        _remove = remove;
    }

    /// <summary>The operator asked to stop this command, so a not-completed result is a stop, not a timeout.</summary>
    public bool StopRequested
    {
        get => _stopRequested;
        internal set => _stopRequested = value;
    }

    internal string Key { get; }

    internal ISandboxRuntimeProvider Provider { get; }

    internal SandboxHandle Handle { get; }

    internal string ExecutionId { get; }

    public void Dispose()
    {
        _remove(this);
    }
}
