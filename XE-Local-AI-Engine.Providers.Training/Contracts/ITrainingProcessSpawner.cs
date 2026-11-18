namespace XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Everything the host needs to identify — and later prove the identity of — one trainer process. Persisted as the
///     run's <c>launch_receipt_json</c> the moment the spawn returns, before a single line of output is read.
/// </summary>
public sealed class TrainingLaunchReceipt
{
    /// <summary>The spawned process id.</summary>
    public required int Pid { get; init; }

    /// <summary>
    ///     Its process-group id, read from <c>/proc</c>. Signaled as a group only when it equals <see cref="Pid" />; any other
    ///     value (the host's group before <c>setsid</c> ran, or 0 when unreadable) means the pid alone is signaled.
    /// </summary>
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

    /// <summary>Opens a pipe to the child's stdin for <see cref="ITrainingProcessHandle.WriteLineAsync" />; train and export spawns leave it off.</summary>
    public bool RedirectStandardInput { get; init; }
}

/// <summary>
///     A spawned, still-running trainer. Distinct from <c>IPythonToolRunner</c>, which is run-to-completion and
///     cannot serve a launch receipt: the receipt has to be persisted the instant the child exists, long before it
///     exits.
/// </summary>
public interface ITrainingProcessHandle : IDisposable
{
    TrainingLaunchReceipt Receipt { get; }

    /// <summary>Merged stdout + stderr, line by line, completing when the child closes both streams.</summary>
    IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken);

    /// <summary>The exit code once the child exited and its output closed; a descendant holding the pipe keeps this waiting.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>The exit code once the child exited and no process is left in the group it leads, without waiting for its output to close.</summary>
    /// <remarks>
    ///     A child that does not provably lead its own group is waited on as <see cref="WaitForExitAsync" /> does, output included,
    ///     and then for the group its pid names to empty, since a receipt read before <c>setsid</c> ran records another group. A descendant
    ///     that left the group is not waited on.
    ///     <see langword="null" /> once <paramref name="cancellationToken" /> is cancelled. This default keeps the output wait of
    ///     <see cref="WaitForExitAsync" />, so a handle that does not override it only ever waits longer.
    /// </remarks>
    async Task<int?> WaitForGroupExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    ///     SIGTERM to the process GROUP. Cooperative by contract: <c>train.py</c> latches
    ///     <c>control.should_training_stop</c> and exits with its own cancelled status, so a cancel maps to
    ///     <c>Cancelled</c> rather than <c>Failed</c>.
    /// </summary>
    void RequestStop();

    /// <summary>SIGTERM then SIGKILL to the process group. The watchdog's and the reaper's escalation.</summary>
    void KillGroup();

    /// <summary>Writes <paramref name="line" /> and a newline to the child's stdin as UTF-8 and flushes; concurrent writers are serialized.</summary>
    /// <exception cref="InvalidOperationException">The spawn did not set <see cref="TrainingSpawnRequest.RedirectStandardInput" />.</exception>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
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

    /// <summary>Null when <c>/proc/[pid]/exe</c> could not be read.</summary>
    public required string? ExecutablePath { get; init; }

    /// <summary>Null when the environment could not be read; empty when it was read and carries no token.</summary>
    public required string? RunToken { get; init; }
}

/// <summary>Reads process identity out of <c>/proc</c> for a process this host does not own a handle to.</summary>
public interface ITrainingProcessInspector
{
    /// <summary>The current facts for <paramref name="processId" />, or null when it is gone or unreadable.</summary>
    TrainingProcessFacts? Inspect(int processId);

    /// <summary>Whether <paramref name="processId" /> is gone, alive, or unreadable; tells apart the two cases a null <see cref="Inspect" /> merges.</summary>
    TrainingProcessPresence ProbePresence(int processId);

    /// <summary>The host's own process group, which no trainer signal may ever target.</summary>
    int HostProcessGroupId { get; }

    /// <summary>Whether any process is left in <paramref name="processGroupId" />, by a signal-0 probe that sends nothing. Never throws.</summary>
    /// <remarks>
    ///     A leader that is gone proves nothing while its group still has members. False off Linux; true for a group no probe can prove
    ///     empty (init's, the caller's, the host's) and for any refusal other than "no such group".
    /// </remarks>
    bool ProcessGroupHasMembers(int processGroupId);

    /// <summary>
    ///     SIGTERM then SIGKILL to <paramref name="processGroupId" />, each skipped only once its leader provably no longer has
    ///     <paramref name="expectedStartTicks" />. Only ever called after a full receipt match.
    /// </summary>
    /// <returns>The leader's presence re-inspected after the kill; a leader that vanished reads gone only once its group is empty too.</returns>
    Task<TrainingProcessPresence> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken = default);

    /// <summary>SIGTERM then SIGKILL to <paramref name="processId" /> alone while it may still have <paramref name="expectedStartTicks" />.</summary>
    /// <returns>Its presence afterwards; a pid that vanished reads gone only once the group it may have started is empty.</returns>
    Task<TrainingProcessPresence> KillProcessAsync(int processId, long expectedStartTicks, CancellationToken cancellationToken = default);
}
