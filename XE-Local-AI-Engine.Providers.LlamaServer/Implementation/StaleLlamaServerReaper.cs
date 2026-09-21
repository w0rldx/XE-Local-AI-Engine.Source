namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Startup <see cref="IHostedService" /> that reaps stale <c>llama-server</c> orphans left by a previous run of
///     THIS app, so restart is reliable regardless of how that run died.
/// </summary>
/// <remarks>
///     The supervisor launches llama-server detached (Linux <c>setsid</c>, Windows Job Object) and tears it down only
///     through graceful DI shutdown, so a hard kill of the host orphans a server still holding its loopback port and
///     GPU VRAM. STRICT MATCHING: a process is reaped ONLY when its executable path is under the app's own binaries
///     root (<see cref="LlamaCppBinaryManager.DefaultLlamaCppBinariesRoot" />), so an unrelated <c>llama-server</c> —
///     Ollama's, say — is never touched; an unresolvable root logs and no-ops.
/// </remarks>
internal sealed class StaleLlamaServerReaper : IHostedService
{
    private readonly string? _binariesRoot;
    private readonly ILogger<StaleLlamaServerReaper> _logger;
    private readonly IStaleLlamaServerProcessScanner _scanner;

    /// <summary>
    ///     Creates the reaper over the process-scan seam. <paramref name="binariesRoot" /> is the directory under which
    ///     ONLY this app's llama-server binaries live; a candidate outside it is never reaped. A <see langword="null" />
    ///     or blank root disables the reap (logged, no-op).
    /// </summary>
    public StaleLlamaServerReaper(IStaleLlamaServerProcessScanner scanner,
        string? binariesRoot,
        ILogger<StaleLlamaServerReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(logger);
        _scanner = scanner;
        _binariesRoot = binariesRoot;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Best-effort and wrapped, so a reaper failure can never block app start. Hosted services start during host
    ///     startup, before the supervisor spawns anything, so it only ever observes orphans from a previous run and
    ///     never this run's own children.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The whole body is guarded: a reaper failure must NEVER block application start. The synchronous OS process
        // scan is wrapped here rather than offloaded — it is a quick one-shot at startup before any request is served.
        try
        {
            Reap();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup llama-server orphan reaper failed; continuing startup.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void Reap()
    {
        if (string.IsNullOrWhiteSpace(_binariesRoot))
        {
            _logger.LogInformation("Skipping llama-server orphan reap: the runtime binaries directory could not be resolved.");
            return;
        }

        var fullRoot = Path.GetFullPath(_binariesRoot);
        var candidates = _scanner.EnumerateLlamaServerProcesses();

        var reaped = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.ExecutablePath is not { Length: > 0 } executablePath || !PathContainment.IsUnderRoot(executablePath, fullRoot))
            {
                continue;
            }

            _logger.LogInformation("Reaping stale llama-server orphan (pid {Pid}) at {Path}.", candidate.Pid, executablePath);
            _scanner.KillProcessTree(candidate.Pid);
            reaped++;
        }

        if (reaped > 0)
        {
            _logger.LogInformation("Reaped {Count} stale llama-server orphan process(es) left by a previous run.", reaped);
        }
    }
}
