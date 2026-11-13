namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>Shields a provider's output pump from its observer: the first exception is logged and the observer is dropped.</summary>
internal sealed class GuardedSandboxOutputObserver : ISandboxOutputObserver
{
    private readonly ISandboxOutputObserver _inner;
    private readonly ILogger _logger;
    private readonly string _executionId;
    private int _faulted;

    private GuardedSandboxOutputObserver(ISandboxOutputObserver inner, ILogger logger, string executionId)
    {
        _inner = inner;
        _logger = logger;
        _executionId = executionId;
    }

    /// <summary>The guarded observer, or <see langword="null" /> when the request carries none.</summary>
    public static GuardedSandboxOutputObserver? Wrap(ISandboxOutputObserver? inner, ILogger logger, string executionId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return inner is null ? null : new GuardedSandboxOutputObserver(inner, logger, executionId);
    }

    public void OnLine(SandboxOutputSource source, string line)
    {
        if (Volatile.Read(ref _faulted) != 0)
        {
            return;
        }

        try
        {
            _inner.OnLine(source, line);
        }
        catch (Exception exception)
        {
            // A broken observer would likely throw on every line; dropping it keeps a flood's pump at full speed.
            if (Interlocked.Exchange(ref _faulted, 1) == 0)
            {
                _logger.LogWarning(exception, "The live output observer of sandbox command {ExecutionId} failed and was detached.", _executionId);
            }
        }
    }
}
