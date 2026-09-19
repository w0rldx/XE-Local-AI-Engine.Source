namespace XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Everything the host needs to identify — and later prove the identity of — one trainer process. Persisted as the
///     run's <c>launch_receipt_json</c> the moment the spawn returns, before a single line of output is read.
/// </summary>
public sealed class TrainingLaunchReceipt
{
    /// <summary>The spawned process id.</summary>
    public required int Pid { get; init; }

    /// <summary>Its process-group id, read from <c>/proc</c> rather than assumed equal to the pid.</summary>
    public required int Pgid { get; init; }

    /// <summary>The resolved <c>/proc/[pid]/exe</c> target at spawn time.</summary>
    public required string? ExecutablePath { get; init; }

    /// <summary>Field 22 of <c>/proc/[pid]/stat</c> — the pid-reuse guard.</summary>
    public required long StartTicks { get; init; }

    /// <summary>
    /// A per-run nonce handed to the child through its environment and read back from
    ///     <c>/proc/[pid]/environ</c>. The one field a recycled pid running the same interpreter cannot forge.
    /// </summary>
    public required string RunToken { get; init; }
}

/// <summary>
///     What a spawn needs. The child's environment is NOT passed in: the provider owns the allowlist and the cache
///     containment (offline flags, HF/torch/triton cache roots), so no caller can widen it by accident.
/// </summary>
public sealed class TrainingSpawnRequest
{
    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string RunToken { get; init; }

    /// <summary>
    ///     Set only for an EXPORT subprocess: the vendored <c>gguf-py</c> package directory the llama.cpp conversion
    ///     scripts import, placed on the child's <c>PYTHONPATH</c>. Null spawns the plain training environment.
    /// </summary>
    public string? GgufPyDirectory { get; init; }
}

/// <summary>
///     A spawned, still-running trainer. Distinct from <c>ITrainingProcessRunner</c>, which is run-to-completion and
///     cannot serve a launch receipt: the receipt has to be persisted the instant the child exists, long before it
///     exits.
/// </summary>
public interface ITrainingProcessHandle : IDisposable
{
    TrainingLaunchReceipt Receipt { get; }

    /// <summary>Merged stdout + stderr, line by line, completing when the child closes both streams.</summary>
    IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken);

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     SIGTERM to the process GROUP. Cooperative by contract: <c>train.py</c> latches
    ///     <c>control.should_training_stop</c> and exits with its own cancelled status, so a cancel maps to
    ///     <c>Cancelled</c> rather than <c>Failed</c>.
    /// </summary>
    void RequestStop();

    /// <summary>SIGTERM then SIGKILL to the process group. The watchdog's and the reaper's escalation.</summary>
    void KillGroup();
}

public interface ITrainingProcessSpawner
{
    /// <exception cref="TrainingRuntimeException">The child could not be started.</exception>
    ITrainingProcessHandle Spawn(TrainingSpawnRequest request);
}

/// <summary>The live facts a reaper compares a persisted <see cref="TrainingLaunchReceipt" /> against.</summary>
public sealed record TrainingProcessFacts
{
    public required int Pgid { get; init; }

    public required long StartTicks { get; init; }

    public required string? ExecutablePath { get; init; }

    public required string? RunToken { get; init; }
}

/// <summary>Reads process identity out of <c>/proc</c> for a process this host does not own a handle to.</summary>
public interface ITrainingProcessInspector
{
    /// <summary>The current facts for <paramref name="processId" />, or null when it is gone or unreadable.</summary>
    TrainingProcessFacts? Inspect(int processId);

    /// <summary>SIGTERM then SIGKILL to <paramref name="processGroupId" />. Only ever called after a full receipt match.</summary>
    Task KillProcessGroupAsync(int processGroupId, CancellationToken cancellationToken = default);
}
