namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Startup <see cref="IHostedService" /> that reaps sandbox children orphaned by a previous run of THIS app, and the stale jails they
///     left behind; the direct structural mirror of <c>StaleProcessReaper</c>.
/// </summary>
/// <remarks>
///     A hard host kill skips <c>Dispose</c> and <c>KillAsync</c>, leaving the child process group running and its jail on disk. Three
///     independent gates apply, because signaling a group is irreversible and a marker is untrusted input after a crash. OWNER LIVENESS:
///     a marker whose owning worker pid is alive is skipped entirely. PID REUSE: the group is signaled only while the leader's start time
///     still matches what launch recorded. PATH OWNERSHIP: <see cref="PathContainment.IsUnderRoot" /> gates deletion on <see cref="SandboxPaths.ContainerRoot" />, and one
///     flagged <see cref="SandboxProcessMarker.PreserveJail" /> never at all. The sweep is best-effort and cannot block startup.
/// </remarks>
public sealed class SandboxOrphanReaper : IHostedService
{
    /// <summary>
    ///     How long an engine-owned scope that NO marker claims must have been active before the sweep will signal it.
    /// </summary>
    /// <remarks>
    ///     Defense in depth behind the pre-registered marker, for the case it cannot cover: a worker whose marker store is unwritable
    ///     launches isolated commands indistinguishable on disk from a previous run's leftovers. A scope that young more likely belongs to
    ///     a worker starting alongside this one, and the mistakes are not symmetric — signaling a live command destroys work
    ///     irrecoverably, while skipping a genuine orphan leaves it to its own <c>RuntimeMaxSec</c>.
    /// </remarks>
    private static readonly TimeSpan UnreferencedScopeGrace = TimeSpan.FromSeconds(30);

    private readonly ISandboxContainmentProbe? _containmentProbe;
    private readonly ISandboxProcessGroupKiller _killer;
    private readonly ISandboxScopeUnitKiller? _scopeKillerOverride;
    private readonly ILogger<SandboxOrphanReaper> _logger;
    private readonly ISandboxMarkerStore _markerStore;
    private readonly string? _containerRootOverride;
    private readonly Func<int, DateTime, bool> _isOwnerAlive;

    public SandboxOrphanReaper(ISandboxMarkerStore markerStore,
        ISandboxProcessGroupKiller killer,
        ILogger<SandboxOrphanReaper> logger,
        ISandboxContainmentProbe? containmentProbe = null)
        : this(markerStore, killer, logger, containmentProbe, scopeKiller: null)
    {
    }

    // The scope killer, container root and owner liveness are injectable so the sweep's DECISIONS are tested without a systemd user
    // manager or a dead engine; signaling a cgroup and deleting a folder are irreversible, so they are asserted.
    internal SandboxOrphanReaper(ISandboxMarkerStore markerStore,
        ISandboxProcessGroupKiller killer,
        ILogger<SandboxOrphanReaper> logger,
        ISandboxContainmentProbe? containmentProbe,
        ISandboxScopeUnitKiller? scopeKiller,
        string? containerRoot = null,
        Func<int, DateTime, bool>? isOwnerAlive = null)
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
        _containerRootOverride = containerRoot;
        _isOwnerAlive = isOwnerAlive ?? SandboxSessionOwner.IsAlive;
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

    /// <summary>Classifies every dead owner's marker, then deletes jails and markers in a second pass.</summary>
    /// <remarks>
    ///     A group that could not be verified dead (<see cref="SandboxGroupKillOutcome.Deferred" />) keeps its marker and its jail, and
    ///     protects that jail from both deletion layers, as a live owner's does. The retry has no age bound: such a marker is tried again
    ///     at every start until its group is proven gone.
    /// </remarks>
    private async Task ReapAsync(CancellationToken cancellationToken)
    {
        // Deliberately NOT short-circuited on an empty marker set: the transient-scope sweep below is the only thing that finds a scope
        // whose marker was never written, which a worker killed between `systemd-run` and the marker hitting disk leaves behind.
        var markers = _markerStore.ReadAll();

        var containerRoot = Path.GetFullPath(_containerRootOverride ?? SandboxPaths.ContainerRoot);
        var reapedGroups = 0;
        var reapedScopes = 0;
        var deletedJails = 0;

        var scopeKiller = _scopeKillerOverride ?? SandboxScopeUnitKiller.CreateOrNull(_containmentProbe?.Containment.FilesystemIsolation);
        var liveScopeUnits = new HashSet<string>(StringComparer.Ordinal);
        // Jails no deletion may touch: a live owner's, and a Deferred group's, whose members may still run in it.
        var protectedJailPaths = new List<string>();
        var classified = new List<KeyValuePair<string, SandboxProcessMarker>>();

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

                // And its jail: no deletion below may touch the folder holding it.
                if (!string.IsNullOrWhiteSpace(marker.JailPath))
                {
                    protectedJailPaths.Add(marker.JailPath);
                }

                continue;
            }

            // Gate 2: only signal the group when the leader is still the process we launched. Classified BEFORE the scope kill, which
            // could take the leader and with it the identity the group is verified by.
            var group = await TryReapProcessGroupAsync(marker, cancellationToken);
            if (group == SandboxGroupKillOutcome.Killed)
            {
                reapedGroups++;
            }

            // The scope's cgroup, when there was one. Unlike the process-group signal this needs no pid-reuse guard: a unit NAME carries a
            // fresh GUID per command and is never recycled, so the only thing it can identify is the command that generated it.
            if (marker.ScopeUnitName is { } unitName && scopeKiller is not null)
            {
                await scopeKiller.KillAsync(unitName, cancellationToken);
                reapedScopes++;
            }

            if (group == SandboxGroupKillOutcome.Deferred)
            {
                // Members may still run in the jail: keep the marker for the next start (no age bound) and the jail with it.
                _logger.LogWarning("Sandbox orphan process group {Pgid} from sandbox {SandboxId} could not be verified dead; its marker and jail are kept for the next start.",
                    marker.ProcessGroupId,
                    marker.SandboxId);
                if (!string.IsNullOrWhiteSpace(marker.JailPath))
                {
                    protectedJailPaths.Add(marker.JailPath);
                }

                continue;
            }

            classified.Add(new KeyValuePair<string, SandboxProcessMarker>(markerId, marker));
        }

        // Each jail is decided only after every marker naming it was classified, so the order markers are read in cannot matter.
        foreach (var (markerId, marker) in classified)
        {
            // Gate 3: delete the jail only when we own the path, it is not a preserved workspace, and no live or Deferred marker shares it.
            if (TryDeleteJail(marker, containerRoot, protectedJailPaths))
            {
                deletedJails++;
            }

            _markerStore.Delete(markerId);
        }

        reapedScopes += await SweepUnreferencedScopesAsync(scopeKiller, liveScopeUnits, cancellationToken);
        var deletedSessionRoots = SweepDeadSessionRoots(containerRoot, protectedJailPaths);

        if (reapedGroups > 0 || reapedScopes > 0 || deletedJails > 0 || deletedSessionRoots > 0)
        {
            _logger.LogInformation(
                "Reaped {Groups} orphaned sandbox process group(s) and {Scopes} transient scope(s), and removed {Jails} stale jail(s) and {SessionRoots} sandbox session folder(s) left by a previous run.",
                reapedGroups,
                reapedScopes,
                deletedJails,
                deletedSessionRoots);
        }
    }

    /// <summary>
    ///     Deletes each session folder directly under the container root whose <see cref="SandboxSessionOwner" /> names a process that is
    ///     gone.
    /// </summary>
    /// <remarks>
    ///     Only <c>Dispose</c> removes a session root, which a killed engine never runs, and Windows writes no markers. A root with no
    ///     owner record, one overlapping a live or Deferred marker's jail, the markers folder and links are always left.
    /// </remarks>
    private int SweepDeadSessionRoots(string containerRoot, IReadOnlyList<string> protectedJailPaths)
    {
        if (!Directory.Exists(containerRoot))
        {
            return 0;
        }

        var markersName = Path.GetFileName(SandboxPaths.MarkersRoot);
        var deleted = 0;
        foreach (var directory in new DirectoryInfo(containerRoot).EnumerateDirectories())
        {
            if (string.Equals(directory.Name, markersName, StringComparison.OrdinalIgnoreCase)
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            // No owner record proves nothing about who uses the folder (an older build may still run beside this one): never deleted.
            var dead = SandboxSessionOwner.ReadFromOrNull(directory.FullName) is { } owner
                       && !_isOwnerAlive(owner.ProcessId, owner.ProcessStartTimeUtc);
            if (!dead || protectedJailPaths.Any(jail => Overlaps(jail, directory.FullName)))
            {
                continue;
            }

            try
            {
                directory.Delete(recursive: true);
                deleted++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not delete the stale sandbox session folder at {SessionRoot}.", directory.FullName);
            }
        }

        return deleted;
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

    private async Task<SandboxGroupKillOutcome> TryReapProcessGroupAsync(SandboxProcessMarker marker, CancellationToken cancellationToken)
    {
        if (marker.ProcessGroupId is not { } processGroupId || marker.LeaderStartTicks is not { } recordedStartTicks)
        {
            // A marker pre-registered before its launch whose owner died before the pid was recorded, or a launch with no signallable
            // group. Nothing to signal — the scope kill covers an isolated workload — and kill(-0) would signal the REAPER's group.
            return SandboxGroupKillOutcome.Gone;
        }

        if (processGroupId <= 1)
        {
            // Belt and braces for a marker that reached disk from an older build or a corrupted write. 0 means "my own
            // group" to kill(2) and 1 is init; neither is ever a sandbox child.
            _logger.LogDebug("Skipping sandbox orphan marker from sandbox {SandboxId}: {Pgid} is not a signallable process group.",
                marker.SandboxId,
                processGroupId);
            return SandboxGroupKillOutcome.Mismatch;
        }

        // The killer verifies the recorded start time AFTER pinning the pid, so a recycled pid is never signalled; an absent leader is
        // not taken for an empty group, and an unreadable identity defers.
        var outcome = await _killer.KillProcessGroupAsync(processGroupId, recordedStartTicks, cancellationToken);
        if (outcome == SandboxGroupKillOutcome.Killed)
        {
            _logger.LogInformation("Reaped orphaned sandbox process group {Pgid} from sandbox {SandboxId}.",
                processGroupId,
                marker.SandboxId);
        }
        else if (outcome == SandboxGroupKillOutcome.Mismatch)
        {
            _logger.LogDebug("Skipping sandbox orphan pgid {Pgid}: the pid now names another process.", processGroupId);
        }

        return outcome;
    }

    /// <summary>Whether deleting one of the two paths would remove the other or part of it: equal, ancestor or descendant.</summary>
    /// <remarks>Both are normalized with trailing separators trimmed; an unparseable path overlaps nothing, so it protects nothing.</remarks>
    private static bool Overlaps(string left, string right)
    {
        string fullLeft;
        string fullRight;
        try
        {
            fullLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            fullRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return string.Equals(fullLeft, fullRight, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
               || PathContainment.IsUnderRoot(fullLeft, fullRight)
               || PathContainment.IsUnderRoot(fullRight, fullLeft);
    }

    private bool TryDeleteJail(SandboxProcessMarker marker, string containerRoot, IReadOnlyList<string> protectedJailPaths)
    {
        if (marker.PreserveJail)
        {
            // An engine-managed trusted host workspace survives kill and restart by contract; the provider itself never
            // deletes it, and neither may the reaper.
            return false;
        }

        if (string.IsNullOrWhiteSpace(marker.JailPath)
            || !PathContainment.IsUnderRoot(marker.JailPath, containerRoot)
            || protectedJailPaths.Any(jail => Overlaps(jail, marker.JailPath)))
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
}
