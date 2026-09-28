namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Startup <see cref="IHostedService" /> that reaps one runtime's stale orphans left by a previous run of THIS app,
///     so restart is reliable regardless of how that run died.
/// </summary>
/// <remarks>
///     A hard kill of the host skips the supervisors' graceful teardown and orphans servers still holding their port
///     and GPU memory. STRICT MATCHING: a process is reaped ONLY when its executable path is under the provider's own
///     binaries root (<see cref="PathContainment" />), so an unrelated server of the same name is never touched; an
///     unresolvable root logs and no-ops. Each provider registers its own instance, so the registration must not dedupe
///     by implementation type (<c>AddHostedService</c> does).
/// </remarks>
public sealed class StaleProcessReaper : IHostedService
{
    private readonly string? _binariesRoot;
    private readonly bool _logExecutablePath;
    private readonly ILogger<StaleProcessReaper> _logger;
    private readonly IStaleProcessScanner _scanner;
    private readonly string _serverName;

    /// <summary>
    ///     Creates the reaper. <paramref name="binariesRoot" /> holds ONLY this app's <paramref name="serverName" />
    ///     binaries; a candidate outside it is never reaped, and a blank root disables the reap.
    /// </summary>
    /// <remarks><paramref name="logExecutablePath" /> chooses whether the per-orphan line names the binary path.</remarks>
    public StaleProcessReaper(IStaleProcessScanner scanner,
        string? binariesRoot,
        string serverName,
        bool logExecutablePath,
        ILogger<StaleProcessReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(logger);
        _scanner = scanner;
        _binariesRoot = binariesRoot;
        _serverName = serverName;
        _logExecutablePath = logExecutablePath;
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
            _logger.LogWarning(ex, "Startup {ServerName} orphan reaper failed; continuing startup.", _serverName);
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
            _logger.LogInformation("Skipping {ServerName} orphan reap: the runtime binaries directory could not be resolved.", _serverName);
            return;
        }

        var fullRoot = Path.GetFullPath(_binariesRoot);
        var candidates = _scanner.EnumerateProcesses();

        var reaped = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.ExecutablePath is not { Length: > 0 } executablePath || !PathContainment.IsUnderRoot(executablePath, fullRoot))
            {
                continue;
            }

            if (_logExecutablePath)
            {
                _logger.LogInformation("Reaping stale {ServerName} orphan (pid {Pid}) at {Path}.", _serverName, candidate.Pid, executablePath);
            }
            else
            {
                _logger.LogInformation("Reaping stale {ServerName} orphan (pid {Pid}).", _serverName, candidate.Pid);
            }

            _scanner.KillProcessTree(candidate.Pid);
            reaped++;
        }

        if (reaped > 0)
        {
            _logger.LogInformation("Reaped {Count} stale {ServerName} orphan process(es) left by a previous run.", reaped, _serverName);
        }
    }
}
