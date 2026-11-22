namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Globalization;
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

        // Every pid a receipt reaped or kept: a kept one may still be ours, or this host's own child, so the path match must not tree-kill it either.
        var claimedPids = new HashSet<int>();
        var reaped = hasReceipts ? ReapByReceipt(_receipts!, claimedPids) : 0;
        if (!hasRoot)
        {
            LogReapedCount(reaped);
            return;
        }

        var fullRoot = Path.GetFullPath(_binariesRoot!);
        foreach (var candidate in _scanner.EnumerateProcesses())
        {
            ProcessStat stat = default;
            if (claimedPids.Contains(candidate.Pid)
                || candidate.ExecutablePath is not { Length: > 0 } executablePath
                || !PathContainment.IsUnderRoot(executablePath, fullRoot)
                || (OperatingSystem.IsLinux() && !IsSomeoneElsesChild(candidate.Pid, out stat)))
            {
                continue;
            }

            if (!OperatingSystem.IsLinux())
            {
                _scanner.KillProcessTree(candidate.Pid);
            }
            else if (_scanner.KillIfSameProcess(candidate.Pid, executablePath, stat.StartTicks) is var result and not StaleProcessKillResult.Killed)
            {
                _logger.LogWarning("The stale {ServerName} orphan (pid {Pid}) under the managed root was not signalled ({Result}): it is gone, changed hands or could not be verified.",
                    _serverName, candidate.Pid, result);
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

            reaped++;
        }

        LogReapedCount(reaped);
    }

    /// <summary>
    ///     True only when <c>/proc</c> names a parent other than this host: a server it spawned whose receipt write failed has none to claim it,
    ///     and a stat that cannot be read establishes no ownership either way. <paramref name="stat" /> is the stat read.
    /// </summary>
    /// <remarks>
    ///     The path match kills through <see cref="IStaleProcessScanner.KillIfSameProcess" /> with that stat's start time, so a pid that changed
    ///     hands since this read is never signalled. A non-leader is signalled alone: its descendants are no longer tree-killed by this pass.
    /// </remarks>
    private bool IsSomeoneElsesChild(int pid, out ProcessStat stat)
    {
        if (_scanner.ReadStat(pid) is not { } read)
        {
            stat = default;
            _logger.LogDebug("Skipping {ServerName} pid {Pid} under the managed root: its stat could not be read, so its owner is unknown.", _serverName, pid);
            return false;
        }

        stat = read;
        if (stat.ParentProcessId == Environment.ProcessId)
        {
            _logger.LogDebug("Skipping {ServerName} pid {Pid} under the managed root: it is this host's own child.", _serverName, pid);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Signals every receipted child that is still exactly the process this node spawned, and removes every receipt it has
    ///     finished with. Returns how many were signalled, and adds each pid signalled or kept to <paramref name="claimedPids" />.
    /// </summary>
    /// <remarks>
    ///     The pid must be live, not this host's child, and carry the recorded executable realpath and start time, read after a pidfd pinned it.
    ///     A recycled pid, another binary, a zombie, an unparseable receipt or one of another boot signals nothing and deletes the receipt; an
    ///     unreadable entry or file, a failed pidfd call, this host's child or, for a receipt bound to a boot, an unreadable current boot id
    ///     signals nothing and keeps it, claimed, so the path match spares it. The pid is resolved directly: a bring-your-own binary may carry any name.
    /// </remarks>
    private int ReapByReceipt(ProcessSpawnReceiptStore receipts, HashSet<int> claimedPids)
    {
        var reaped = 0;
        var currentBoot = _scanner.CurrentBootId;
        foreach (var stored in receipts.ReadAll())
        {
            var file = stored.File;
            var receipt = stored.Receipt;
            try
            {
                if (stored.Unreadable)
                {
                    // A read failure says nothing about the file, so it stays, and the pid its name records is spared by the path match too.
                    _logger.LogWarning("A {ServerName} spawn receipt could not be read; it was kept and nothing was signalled.", _serverName);
                    if (int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var filePid))
                    {
                        _ = claimedPids.Add(filePid);
                    }

                    continue;
                }

                if (receipt is null)
                {
                    _logger.LogWarning("A {ServerName} spawn receipt is not valid; it was removed and nothing was signalled.", _serverName);
                }
                else if (receipt.BootId is not null && currentBoot is null)
                {
                    _logger.LogWarning("The {ServerName} spawn receipt for pid {Pid} ({Label}) records a boot, but this boot's id could not be read; nothing was signalled and its receipt was kept.",
                        _serverName, receipt.Pid, receipt.Label);
                    _ = claimedPids.Add(receipt.Pid);
                    continue;
                }
                else if (receipt.BootId is not null && !string.Equals(receipt.BootId, currentBoot, StringComparison.Ordinal))
                {
                    // No process outlives its boot: the pid now names a process of this boot, which the path match judges on its own.
                    _logger.LogInformation("A {ServerName} spawn receipt for pid {Pid} ({Label}) was written in a previous boot; it was removed and nothing was signalled.",
                        _serverName, receipt.Pid, receipt.Label);
                }
                else if (_scanner.ReadStat(receipt.Pid) is { } live && live.ParentProcessId == Environment.ProcessId)
                {
                    // This host's own child: something spawned before the reaper ran. Its receipt is live, so it stays, and the path match spares it.
                    _ = claimedPids.Add(receipt.Pid);
                    continue;
                }
                else
                {
                    var result = _scanner.KillIfSameProcess(receipt.Pid, receipt.ExecutablePath, receipt.StartTicks);
                    if (result == StaleProcessKillResult.OwnChild)
                    {
                        _ = claimedPids.Add(receipt.Pid);
                        continue;
                    }

                    if (result is StaleProcessKillResult.Unreadable or StaleProcessKillResult.Deferred)
                    {
                        _logger.LogWarning("The {ServerName} orphan recorded for pid {Pid} ({Label}) could not be verified ({Result}); nothing was signalled and its receipt was kept.",
                            _serverName, receipt.Pid, receipt.Label, result);
                        _ = claimedPids.Add(receipt.Pid);
                        continue;
                    }

                    if (result == StaleProcessKillResult.Killed)
                    {
                        _logger.LogInformation("Reaping stale {ServerName} orphan (pid {Pid}, {Label}) recorded by this node's spawn receipt.",
                            _serverName, receipt.Pid, receipt.Label);
                        _ = claimedPids.Add(receipt.Pid);
                        reaped++;
                    }
                    else
                    {
                        _logger.LogInformation("A {ServerName} spawn receipt for pid {Pid} ({Label}) no longer matches a live process; it was removed and nothing was signalled.",
                            _serverName, receipt.Pid, receipt.Label);
                    }
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

    private void LogReapedCount(int reaped)
    {
        if (reaped > 0)
        {
            _logger.LogInformation("Reaped {Count} stale {ServerName} orphan process(es) left by a previous run.", reaped, _serverName);
        }
    }
}
