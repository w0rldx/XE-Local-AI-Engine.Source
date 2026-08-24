namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     Startup <see cref="IHostedService" /> that reaps sandbox children orphaned by a previous run of THIS app, and the
///     stale jails they left behind. It is the direct structural mirror of <c>StaleLlamaServerReaper</c>.
///     <para>
///         The provider tears its children down through <c>Dispose</c> and <c>KillAsync</c>, both of which run only on a
///         graceful DI shutdown or an explicit kill. A hard host kill — the documented behaviour of <c>aspire stop</c>
///         on this stack — skips both, leaving the child process group running and its jail directory on disk. Reaping
///         on the next start makes restart clean regardless of how the previous run died.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         <b>Three independent safety gates</b>, because signalling a process group is irreversible and a marker is
///         untrusted input after a crash:
///     </para>
///     <list type="number">
///         <item>
///             <b>Owner liveness.</b> A marker whose owning worker pid is still alive belongs to a running worker (this
///             one, or a second instance) and is skipped entirely — the reaper never touches a live run's children.
///         </item>
///         <item>
///             <b>Pid-reuse guard.</b> The kernel may have recycled the recorded process-group id onto an unrelated
///             process. The group is signalled only when the leader's start time still matches the value recorded at
///             launch, so a recycled pid is skipped rather than killed.
///         </item>
///         <item>
///             <b>Strict path ownership.</b> A jail is deleted only when it lies under
///             <see cref="SandboxPaths.ContainerRoot" />, parity with <c>StaleLlamaServerReaper.IsUnderRoot</c>. A
///             marker naming a path outside it gets its process group reaped (if the first two gates pass) but nothing
///             deleted. A jail flagged <see cref="SandboxProcessMarker.PreserveJail" /> — an engine-managed trusted host
///             workspace — is never deleted at all.
///         </item>
///     </list>
///     <para>
///         The whole sweep is best-effort and wrapped so a reaper failure can never block application start. Hosted
///         services start before any request is served and before the provider spawns anything, so it only ever observes
///         orphans from a previous run.
///     </para>
/// </remarks>
public sealed class SandboxOrphanReaper : IHostedService
{
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

    // The scope killer is injectable so the sweep's DECISIONS can be tested without a systemd user manager: which unit
    // a live worker still claims, which is an orphan, and which name this engine did not generate. Signalling a cgroup
    // is irreversible, so those decisions are worth asserting rather than reasoning about.
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
            await ReapAsync(cancellationToken).ConfigureAwait(false);
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
        // Deliberately NOT short-circuited on an empty marker set: the transient-scope sweep below is the only thing
        // that finds a scope whose marker was never written. A worker killed between `systemd-run` creating the scope
        // and the marker hitting disk leaves an isolated workload running inside a cgroup nothing references, and with
        // an early return here its RuntimeMaxSec was the only thing that would ever stop it. An empty set costs one
        // unit listing on a host that has the mechanism, and nothing at all on a host that does not.
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

            // The scope's cgroup, when there was one. Unlike the process-group signal this needs no pid-reuse guard:
            // a unit NAME carries a fresh GUID per command and is never recycled, so the only thing it can ever
            // identify is the command that generated it.
            if (marker.ScopeUnitName is { } unitName && scopeKiller is not null)
            {
                await scopeKiller.KillAsync(unitName, cancellationToken).ConfigureAwait(false);
                reapedScopes++;
            }

            // Gate 2: only signal the group when the leader is still the process we launched.
            if (await TryReapProcessGroupAsync(marker, cancellationToken).ConfigureAwait(false))
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

        reapedScopes += await SweepUnreferencedScopesAsync(scopeKiller, liveScopeUnits, cancellationToken).ConfigureAwait(false);

        if (reapedGroups > 0 || reapedScopes > 0 || deletedJails > 0)
        {
            _logger.LogInformation(
                "Reaped {Groups} orphaned sandbox process group(s) and {Scopes} transient scope(s), and removed {Jails} stale jail(s) left by a previous run.",
                reapedGroups,
                reapedScopes,
                deletedJails);
        }
    }

    /// <summary>
    ///     Kills every transient scope this engine owns that no LIVE worker still claims.
    ///     <para>
    ///         A scope with <c>--collect</c> disappears on its own as soon as its cgroup is empty, so anything still
    ///         loaded here has processes in it. The only such scope that legitimately exists at startup belongs to a
    ///         second worker instance that is still running — which is exactly the set collected above from markers
    ///         with live owners, and exactly the set skipped here. Everything else is a jail whose supervising engine
    ///         died, and whose <c>RuntimeMaxSec</c> would otherwise be the only thing that ever stopped it.
    ///     </para>
    ///     <para>
    ///         The unit-name shape is checked twice — once when listing, once inside the killer — because this is the
    ///         one place the reaper acts on a name it did not read from its own marker file.
    ///     </para>
    /// </summary>
    private async Task<int> SweepUnreferencedScopesAsync(ISandboxScopeUnitKiller? scopeKiller,
        IReadOnlySet<string> liveScopeUnits,
        CancellationToken cancellationToken)
    {
        if (scopeKiller is null)
        {
            return 0;
        }

        var swept = 0;
        foreach (var unitName in scopeKiller.ListEngineOwnedUnits())
        {
            if (liveScopeUnits.Contains(unitName))
            {
                continue;
            }

            _logger.LogInformation("Reaping orphaned sandbox scope {Unit} left by a previous run.", unitName);
            await scopeKiller.KillAsync(unitName, cancellationToken).ConfigureAwait(false);
            swept++;
        }

        return swept;
    }

    private async Task<bool> TryReapProcessGroupAsync(SandboxProcessMarker marker, CancellationToken cancellationToken)
    {
        var currentStartTicks = _killer.GetProcessStartTicks(marker.ProcessGroupId);
        if (currentStartTicks is null)
        {
            // The group leader is already gone — the common case for a short-lived command. Nothing to signal.
            return false;
        }

        if (currentStartTicks != marker.LeaderStartTicks)
        {
            // The pid was recycled onto an unrelated process. Killing its group would take out something that was never
            // ours, so refuse — the stale marker is simply discarded by the caller.
            _logger.LogDebug("Skipping sandbox orphan pgid {Pgid}: the pid was recycled (start ticks {Actual} != recorded {Recorded}).",
                marker.ProcessGroupId,
                currentStartTicks,
                marker.LeaderStartTicks);
            return false;
        }

        _logger.LogInformation("Reaping orphaned sandbox process group {Pgid} from sandbox {SandboxId}.",
            marker.ProcessGroupId,
            marker.SandboxId);
        await _killer.KillProcessGroupAsync(marker.ProcessGroupId, cancellationToken).ConfigureAwait(false);
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
