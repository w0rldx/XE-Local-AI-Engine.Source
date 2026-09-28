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
///     A hard host kill orphans servers still holding their port and GPU memory. STRICT MATCHING: reaped only under the provider's
///     binaries root (<see cref="PathContainment" />), or when a <see cref="ProcessSpawnReceipt" /> this node wrote matches pid,
///     <c>/proc</c> start time and executable realpath. Registration must not dedupe by type (<c>AddHostedService</c> does).
///     Details: wiki 03, "Startup orphan reap and spawn receipts".
/// </remarks>
public sealed class StaleProcessReaper : IHostedService
{
    private readonly string? _binariesRoot;
    private readonly bool _logExecutablePath;
    private readonly ILogger<StaleProcessReaper> _logger;
    private readonly ProcessSpawnReceiptStore? _receipts;
    private readonly IStaleProcessScanner _scanner;
    private readonly string _serverName;

    /// <summary>
    ///     Creates the reaper. <paramref name="binariesRoot" /> holds ONLY this app's <paramref name="serverName" />
    ///     binaries; a candidate outside it is never path-reaped, and a blank root disables the path reap.
    /// </summary>
    /// <remarks>
    ///     <paramref name="logExecutablePath" /> chooses whether the per-orphan line names the binary path. <paramref name="receipts" />
    ///     is the store the provider's supervisor writes; without one only the binaries-root match applies.
    /// </remarks>
    public StaleProcessReaper(IStaleProcessScanner scanner,
        string? binariesRoot,
        string serverName,
        bool logExecutablePath,
        ILogger<StaleProcessReaper> logger,
        ProcessSpawnReceiptStore? receipts = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(logger);
        _scanner = scanner;
        _binariesRoot = binariesRoot;
        _serverName = serverName;
        _logExecutablePath = logExecutablePath;
        _logger = logger;
        _receipts = receipts;
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
        var hasRoot = !string.IsNullOrWhiteSpace(_binariesRoot);
        var hasReceipts = _receipts is { IsEnabled: true };
        if (!hasRoot)
        {
            _logger.LogInformation("Skipping {ServerName} orphan reap: the runtime binaries directory could not be resolved.", _serverName);
            if (!hasReceipts)
            {
                return;
            }
        }

        var reapedPids = hasReceipts ? ReapByReceipt(_receipts!) : [];
        var reaped = reapedPids.Count;
        if (!hasRoot)
        {
            LogReapedCount(reaped);
            return;
        }

        var fullRoot = Path.GetFullPath(_binariesRoot!);
        foreach (var candidate in _scanner.EnumerateProcesses())
        {
            if (reapedPids.Contains(candidate.Pid)
                || candidate.ExecutablePath is not { Length: > 0 } executablePath
                || !PathContainment.IsUnderRoot(executablePath, fullRoot))
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

        LogReapedCount(reaped);
    }

    /// <summary>
    ///     Signals every receipted child that is still exactly the process this node spawned, and removes every receipt it has
    ///     finished with. Returns the pids signalled.
    /// </summary>
    /// <remarks>
    ///     All of these must hold before a signal: the pid is live and not this host's own child,
    ///     its executable realpath equals the recorded one, and its <c>/proc</c> start time equals the recorded one, re-read again
    ///     immediately before the signal. Anything short of that (a recycled pid, another binary, an unreadable entry, an
    ///     unparseable receipt) signals nothing and deletes the receipt, which can then never match again. The pid is resolved
    ///     directly, not through the name-filtered scan, because a bring-your-own binary may carry any file name.
    /// </remarks>
    private HashSet<int> ReapByReceipt(ProcessSpawnReceiptStore receipts)
    {
        var reaped = new HashSet<int>();
        foreach (var (file, receipt) in receipts.ReadAll())
        {
            try
            {
                if (receipt is null)
                {
                    _logger.LogWarning("A {ServerName} spawn receipt could not be read; it was removed and nothing was signalled.", _serverName);
                }
                else if (_scanner.ReadStat(receipt.Pid) is { } live && live.ParentProcessId == Environment.ProcessId)
                {
                    // This host's own child: something spawned before the reaper ran. Its receipt is live, so it stays.
                    continue;
                }
                else if (!Matches(receipt))
                {
                    _logger.LogInformation("A {ServerName} spawn receipt for pid {Pid} ({Label}) no longer matches a live process; it was removed and nothing was signalled.",
                        _serverName, receipt.Pid, receipt.Label);
                }
                else if (_scanner.KillIfSameProcess(receipt.Pid, receipt.StartTicks))
                {
                    _logger.LogInformation("Reaping stale {ServerName} orphan (pid {Pid}, {Label}) recorded by this node's spawn receipt.",
                        _serverName, receipt.Pid, receipt.Label);
                    _ = reaped.Add(receipt.Pid);
                }
                else
                {
                    _logger.LogWarning("The {ServerName} orphan recorded for pid {Pid} ({Label}) changed identity before it could be signalled; nothing was signalled.",
                        _serverName, receipt.Pid, receipt.Label);
                }

                receipts.Delete(file);
            }
            catch (Exception exception)
            {
                // One bad receipt must not abandon the rest; it stays on disk for the next startup.
                _logger.LogWarning(exception, "Reaping a {ServerName} spawn receipt failed; it was kept for the next startup.", _serverName);
            }
        }

        return reaped;
    }

    /// <summary>True only when the pid runs the recorded executable realpath AND carries the recorded start time.</summary>
    private bool Matches(ProcessSpawnReceipt receipt) =>
        _scanner.ReadExecutablePath(receipt.Pid) is { } executablePath
        && string.Equals(executablePath, receipt.ExecutablePath, StringComparison.Ordinal)
        && _scanner.ReadStat(receipt.Pid) is { } stat
        && stat.StartTicks == receipt.StartTicks;

    private void LogReapedCount(int reaped)
    {
        if (reaped > 0)
        {
            _logger.LogInformation("Reaped {Count} stale {ServerName} orphan process(es) left by a previous run.", reaped, _serverName);
        }
    }
}
