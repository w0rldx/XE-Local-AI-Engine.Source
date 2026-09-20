namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     Startup <see cref="IHostedService" /> that reaps sandbox children orphaned by a previous run of THIS app, and the stale jails they
///     left behind; the direct structural mirror of <c>StaleLlamaServerReaper</c>.
/// </summary>
/// <remarks>
///     A hard host kill skips <c>Dispose</c> and <c>KillAsync</c>, leaving the child process group running and its jail on disk. Three
///     independent gates apply, because signalling a group is irreversible and a marker is untrusted input after a crash. OWNER LIVENESS:
///     a marker whose owning worker pid is alive is skipped entirely. PID REUSE: the group is signalled only while the leader's start time
///     still matches what launch recorded. PATH OWNERSHIP: a jail is deleted only under <see cref="SandboxPaths.ContainerRoot" />, and one
///     flagged <see cref="SandboxProcessMarker.PreserveJail" /> never at all. The sweep is best-effort and cannot block startup.
/// </remarks>
public sealed class SandboxOrphanReaper : IHostedService
{
    /// <summary>
    ///     How long an engine-owned scope that NO marker claims must have been active before the sweep will signal it.
    /// </summary>
    /// <remarks>
    ///     Defence in depth behind the pre-registered marker, for the case it cannot cover: a worker whose marker store is unwritable
    ///     launches isolated commands indistinguishable on disk from a previous run's leftovers. A scope that young more likely belongs to
    ///     a worker starting alongside this one, and the mistakes are not symmetric — signalling a live command destroys work
    ///     irrecoverably, while skipping a genuine orphan leaves it to its own <c>RuntimeMaxSec</c>.
    /// </remarks>
    private static readonly TimeSpan UnreferencedScopeGrace = TimeSpan.FromSeconds(30);

    private readonly ISandboxContainmentProbe? _containmentProbe;
    private readonly ISandboxProcessGroupKiller _killer;
    private readonly ISandboxScopeUnitKiller? _scopeKillerOverride;
    private readonly ILogger<SandboxOrphanReaper> _logger;
    private readonly ISandboxMarkerStore _markerStore;

    public SandboxOrphanReaper(ISandboxMarkerStore markerStore,
        ISandboxProcessGroupKiller killer,
        ILogger<SandboxOrphanReaper> logger,
        ISandboxContainmentProbe? containmentProbe = null)
        : this(markerStore, killer, logger, containmentProbe, scopeKiller: null)
    {
    }

    // The scope killer is injectable so the sweep's DECISIONS can be tested without a systemd user manager: which unit a live worker
    // claims, which is an orphan, which name this engine did not generate. Signalling a cgroup is irreversible, so they are asserted.
    internal SandboxOrphanReaper(ISandboxMarkerStore markerStore,
        ISandboxProcessGroupKiller killer,
        ILogger<SandboxOrphanReaper> logger,
        ISandboxContainmentProbe? containmentProbe,
        ISandboxScopeUnitKiller? scopeKiller)
    {
        ArgumentNullException.ThrowIfNull(markerStore);
        ArgumentNullException.ThrowIfNull(killer);
        ArgumentNullException.ThrowIfNull(logger);
        _markerStore = markerStore;
        _killer = killer;
        _logger = logger;
        // Optional so the existing marker-only tests construct the reaper unchanged. When present it supplies the
        // systemctl path and bus address the transient-scope sweep needs.
        _containmentProbe = containmentProbe;
        _scopeKillerOverride = scopeKiller;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The whole body is guarded: a reaper failure must NEVER block application start.
        try
        {
            await ReapAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Startup sandbox orphan reaper failed; continuing startup.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task ReapAsync(CancellationToken cancellationToken)
    {
        // Deliberately NOT short-circuited on an empty marker set: the transient-scope sweep below is the only thing that finds a scope
        // whose marker was never written, which a worker killed between `systemd-run` and the marker hitting disk leaves behind.
        var markers = _markerStore.ReadAll();

        var containerRoot = Path.GetFullPath(SandboxPaths.ContainerRoot);
        var reapedGroups = 0;
        var reapedScopes = 0;
        var deletedJails = 0;

        var scopeKiller = _scopeKillerOverride ?? SandboxScopeUnitKiller.TryCreate(_containmentProbe?.Containment.FilesystemIsolation);
        var liveScopeUnits = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (markerId, marker) in markers)
        {
            // Gate 1: a live owner means a live worker owns this child. Leave it strictly alone — and leave its marker
            // in place, since deleting it would blind the reaper to that child if the owner later crashes.
            if (_killer.IsProcessAlive(marker.OwnerProcessId))
            {
                if (marker.ScopeUnitName is { } liveUnit)
                {
                    // Remember it: the scope sweep below must not touch a unit that belongs to a worker still running.
                    _ = liveScopeUnits.Add(liveUnit);
                }

                continue;
            }

            // The scope's cgroup, when there was one. Unlike the process-group signal this needs no pid-reuse guard: a unit NAME carries a
            // fresh GUID per command and is never recycled, so the only thing it can identify is the command that generated it.
            if (marker.ScopeUnitName is { } unitName && scopeKiller is not null)
            {
                await scopeKiller.KillAsync(unitName, cancellationToken);
                reapedScopes++;
            }

            // Gate 2: only signal the group when the leader is still the process we launched.
            if (await TryReapProcessGroupAsync(marker, cancellationToken))
            {
                reapedGroups++;
            }

            // Gate 3: delete the jail only when we own the path and it is not a preserved workspace.
            if (TryDeleteJail(marker, containerRoot))
            {
                deletedJails++;
            }

            _markerStore.Delete(markerId);
        }

        reapedScopes += await SweepUnreferencedScopesAsync(scopeKiller, liveScopeUnits, cancellationToken);

        if (reapedGroups > 0 || reapedScopes > 0 || deletedJails > 0)
        {
            _logger.LogInformation("Reaped {Groups} orphaned sandbox process group(s) and {Scopes} transient scope(s), and removed {Jails} stale jail(s) left by a previous run.",
                reapedGroups,
                reapedScopes,
                deletedJails);
        }
    }

    /// <summary>Kills every transient scope this engine owns that no LIVE worker claims and that is old enough to be one.</summary>
    /// <remarks>
    ///     A scope with <c>--collect</c> disappears once its cgroup is empty, so anything still loaded has processes in it, and the only
    ///     one that legitimately exists at startup belongs to a second live worker — the set skipped here. Two independent reasons leave a
    ///     unit alone, because the signal is irreversible: a live worker's marker names it, the provider pre-registering that marker BEFORE
    ///     the launch so there is no unreferenced window; or it has been active for less than <see cref="UnreferencedScopeGrace" />, or its
    ///     age was unreported. The unit-name shape is checked twice, this being the one name not read from a marker file.
    /// </remarks>
    private async Task<int> SweepUnreferencedScopesAsync(ISandboxScopeUnitKiller? scopeKiller,
        IReadOnlySet<string> liveScopeUnits,
        CancellationToken cancellationToken)
    {
        if (scopeKiller is null)
        {
            return 0;
        }

        var swept = 0;
        foreach (var unit in scopeKiller.ListEngineOwnedUnits())
        {
            if (liveScopeUnits.Contains(unit.UnitName))
            {
                continue;
            }

            if (unit.ActiveFor is not { } activeFor)
            {
                _logger.LogDebug("Leaving unreferenced sandbox scope {Unit} alone: the user manager did not report how long it has been active.",
                    unit.UnitName);
                continue;
            }

            if (activeFor < UnreferencedScopeGrace)
            {
                _logger.LogDebug("Leaving unreferenced sandbox scope {Unit} alone: it has only been active for {ActiveFor}, inside the {Grace} grace a starting worker gets.",
                    unit.UnitName,
                    activeFor,
                    UnreferencedScopeGrace);
                continue;
            }

            _logger.LogInformation("Reaping orphaned sandbox scope {Unit}, active for {ActiveFor} and claimed by no live worker.",
                unit.UnitName,
                activeFor);
            await scopeKiller.KillAsync(unit.UnitName, cancellationToken);
            swept++;
        }

        return swept;
    }

    private async Task<bool> TryReapProcessGroupAsync(SandboxProcessMarker marker, CancellationToken cancellationToken)
    {
        if (marker.ProcessGroupId is not { } processGroupId || marker.LeaderStartTicks is not { } recordedStartTicks)
        {
            // A marker pre-registered before its launch whose owner died before the pid was recorded, or a launch with no signallable
            // group. Nothing to signal — the scope kill above covers an isolated workload — and kill(-0) would signal the REAPER's group.
            return false;
        }

        if (processGroupId <= 1)
        {
            // Belt and braces for a marker that reached disk from an older build or a corrupted write. 0 means "my own
            // group" to kill(2) and 1 is init; neither is ever a sandbox child.
            _logger.LogDebug("Skipping sandbox orphan marker from sandbox {SandboxId}: {Pgid} is not a signallable process group.",
                marker.SandboxId,
                processGroupId);
            return false;
        }

        var currentStartTicks = _killer.GetProcessStartTicks(processGroupId);
        if (currentStartTicks is null)
        {
            // The group leader is already gone — the common case for a short-lived command. Nothing to signal.
            return false;
        }

        if (currentStartTicks != recordedStartTicks)
        {
            // The pid was recycled onto an unrelated process. Killing its group would take out something that was never
            // ours, so refuse — the stale marker is simply discarded by the caller.
            _logger.LogDebug("Skipping sandbox orphan pgid {Pgid}: the pid was recycled (start ticks {Actual} != recorded {Recorded}).",
                processGroupId,
                currentStartTicks,
                recordedStartTicks);
            return false;
        }

        _logger.LogInformation("Reaping orphaned sandbox process group {Pgid} from sandbox {SandboxId}.",
            processGroupId,
            marker.SandboxId);
        await _killer.KillProcessGroupAsync(processGroupId, cancellationToken);
        return true;
    }

    private bool TryDeleteJail(SandboxProcessMarker marker, string containerRoot)
    {
        if (marker.PreserveJail)
        {
            // An engine-managed trusted host workspace survives kill and restart by contract; the provider itself never
            // deletes it, and neither may the reaper.
            return false;
        }

        if (string.IsNullOrWhiteSpace(marker.JailPath) || !IsUnderRoot(marker.JailPath, containerRoot))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(marker.JailPath))
            {
                return false;
            }

            Directory.Delete(marker.JailPath, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort teardown, parity with the provider's own jail deletion.
            _logger.LogDebug(exception, "Could not delete the stale sandbox jail at {JailPath}.", marker.JailPath);
            return false;
        }
    }

    /// <summary>
    ///     <see langword="true" /> when <paramref name="path" /> is a descendant of <paramref name="root" />. The
    ///     trailing-separator guard prevents a sibling-prefix false match, parity with
    ///     <c>StaleLlamaServerReaper.IsUnderRoot</c>.
    /// </summary>
    private static bool IsUnderRoot(string path, string root)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unparseable path can never be under our root.
            return false;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(rootWithSeparator, comparison);
    }
}
