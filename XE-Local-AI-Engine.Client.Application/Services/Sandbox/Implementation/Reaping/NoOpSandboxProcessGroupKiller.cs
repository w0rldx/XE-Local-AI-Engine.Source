namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>
///     The <see cref="ISandboxProcessGroupKiller" /> registered on platforms with no process-group mechanism. Sandbox
///     markers are only ever written for a <c>setsid</c> launch, which is Linux-only, so off Linux there is nothing to
///     reap — and this keeps the <c>/proc</c> reads and the libc <c>kill()</c> import off a platform that has neither,
///     rather than relying on their error handling to absorb the difference.
///     <para>
///         It reports every process as absent, so <see cref="SandboxOrphanReaper" />'s own logic is unchanged and stays
///         testable on any host: it simply finds nothing to do.
///     </para>
/// </summary>
public sealed class NoOpSandboxProcessGroupKiller : ISandboxProcessGroupKiller
{
    /// <inheritdoc />
    public long? GetProcessStartTicks(int processId)
    {
        return null;
    }

    /// <inheritdoc />
    public bool IsProcessAlive(int processId)
    {
        return false;
    }

    /// <inheritdoc />
    public Task KillProcessGroupAsync(int processGroupId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
