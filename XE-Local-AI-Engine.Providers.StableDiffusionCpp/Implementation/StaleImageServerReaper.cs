namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Startup <see cref="IHostedService" /> that reaps stale <c>sd-server</c> orphans left by a previous run of THIS
///     app, mirroring <c>StaleLlamaServerReaper</c>.
/// </summary>
/// <remarks>
///     The supervisor launches sd-server detached (Linux <c>setsid</c> / Windows Job Object) and tears it down only via graceful DI
///     shutdown; a hard kill of the host skips that path, orphaning the daemon while it still holds its loopback port and GPU VRAM, so
///     reaping on the next start makes restart reliable however the previous run died. <b>Strict matching:</b> a process is reaped ONLY
///     when its executable path is under <see cref="StableDiffusionCppBinaryManager.DefaultStableDiffusionBinariesRoot" />, and an
///     unresolvable root logs and no-ops. Best-effort throughout, and it runs before the supervisor spawns anything.
/// </remarks>
internal sealed class StaleImageServerReaper : IHostedService
{
    private readonly string? _binariesRoot;
    private readonly ILogger<StaleImageServerReaper> _logger;
    private readonly IStaleImageServerProcessScanner _scanner;

    /// <summary>
    ///     Creates the reaper over the process-scan seam. <paramref name="binariesRoot" /> is the directory under which
    ///     ONLY this app's sd-server binaries live; a candidate outside it is never reaped. A <see langword="null" />
    ///     or blank root disables the reap (logged, no-op).
    /// </summary>
    public StaleImageServerReaper(IStaleImageServerProcessScanner scanner,
        string? binariesRoot,
        ILogger<StaleImageServerReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(logger);
        _scanner = scanner;
        _binariesRoot = binariesRoot;
        _logger = logger;
    }

    /// <inheritdoc />
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
            _logger.LogWarning(ex, "Startup sd-server orphan reaper failed; continuing startup.");
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
            _logger.LogInformation("Skipping sd-server orphan reap: the runtime binaries directory could not be resolved.");
            return;
        }

        var fullRoot = Path.GetFullPath(_binariesRoot);
        var candidates = _scanner.EnumerateImageServerProcesses();

        var reaped = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.ExecutablePath is not { Length: > 0 } executablePath || !PathContainment.IsUnderRoot(executablePath, fullRoot))
            {
                continue;
            }

            _logger.LogInformation("Reaping stale sd-server orphan (pid {Pid}) at {Path}.", candidate.Pid, executablePath);
            _scanner.KillProcessTree(candidate.Pid);
            reaped++;
        }

        if (reaped > 0)
        {
            _logger.LogInformation("Reaped {Count} stale sd-server orphan process(es) left by a previous run.", reaped);
        }
    }
}
