namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Health and introspection half of <see cref="LlamaServerProcessSupervisor" />: the diagnostics surface that
///     probes each owned process for liveness and reports the running count and per-process runtime info.
/// </summary>
public sealed partial class LlamaServerProcessSupervisor
{
    /// <inheritdoc />
    public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct)
    {
        // Snapshot the current processes and probe each one's liveness for the diagnostics surface.
        var snapshot = _processes.ToArray();
        return CheckHealthCoreAsync(snapshot, ct);
    }

    /// <inheritdoc />
    public int CountRunningProcesses()
    {
        // Hot-path count: a synchronous in-memory read of the process table with NO health/HTTP probe. Only handles that
        // have not exited count; the idle reaper removes dead entries, but a just-crashed handle may linger until then.
        var count = 0;
        foreach (var (_, running) in _processes)
        {
            if (!running.Handle.HasExited)
            {
                count++;
            }
        }

        return count;
    }

    /// <inheritdoc />
    public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Synchronous in-memory read (no HTTP): the effective context was captured once after readiness. Null when the
        // process is not running, has exited, or /props did not report a usable value.
        var key = new ProcessKey(modelName, role);
        if (!_processes.TryGetValue(key, out var running)
            || running.Handle.HasExited
            || running.EffectiveContextTokens is not { } effectiveContext)
        {
            return null;
        }

        // A profiling-owned process is excluded: its context comes from explore/replay launch args, not from serving
        // policy, so reporting it would size a chat's context budget off a measurement spawn. The exclusive operation
        // that pinned it is the exception — that window IS the one its own measurement runs in, and withholding it
        // leaves a benchmark's context admission nothing to size against, failing the run it just warmed.
        return !running.IsProfilingOwned || ReferenceEquals(running, GetOwnExclusiveProfilingProcess(key, out _))
            ? new LlamaServerRuntimeInfo(effectiveContext)
            : null;
    }

    private async Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthCoreAsync(KeyValuePair<ProcessKey, RunningProcess>[] snapshot,
        CancellationToken ct)
    {
        var healths = new List<LlamaServerProcessHealth>(snapshot.Length);
        foreach (var (key, running) in snapshot)
        {
            if (running.Handle.HasExited)
            {
                healths.Add(new LlamaServerProcessHealth(key.ModelName, key.Role, IsResponsive: false, "Process has exited.", HasExited: true));
                continue;
            }

            var responsive = await _healthProbe.CheckResponsiveAsync(running.Endpoint.BaseAddress, ct).ConfigureAwait(false);
            healths.Add(new LlamaServerProcessHealth(key.ModelName,
                key.Role,
                responsive,
                responsive ? "Responsive." : "Not responding to health probe."));
        }

        return healths;
    }
}
