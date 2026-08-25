namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Decision coverage for <see cref="SandboxOrphanReaper" />, mirroring <c>StaleLlamaServerReaperTests</c>. The kill
///     seam is faked so the reaper's three safety gates can be asserted without signalling a real process group —
///     signalling is irreversible, and the interesting cases are precisely the ones where it must NOT happen.
/// </summary>
public sealed class SandboxOrphanReaperTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    public async Task SandboxOrphanReaper_WhenMarkerIsOrphaned_KillsTheGroupAndDeletesTheJail()
    {
        var jail = CreateJailUnderContainerRoot();
        var killer = new FakeKiller();
        // The owning worker is gone and the group leader still matches what was recorded: a genuine orphan.
        killer.StartTicks[4242] = 999L;
        var store = new FakeMarkerStore(Marker(processGroupId: 4242, leaderStartTicks: 999L, jailPath: jail));

        await CreateReaper(store, killer).StartAsync(CancellationToken.None);

        AssertEx.Contains(killer.Killed, 4242);
        AssertEx.False(Directory.Exists(jail), "the stale jail must be removed");
        AssertEx.Empty(store.Remaining);
    }

    [Test]
    public async Task SandboxOrphanReaper_WhenOwningWorkerIsStillAlive_LeavesTheChildAndItsMarkerAlone()
    {
        // Gate 1. A live owner means a live worker — this one, or a second instance. Reaping its children would kill
        // work in progress, and deleting the marker would blind us to that child if the owner later crashes.
        var jail = CreateJailUnderContainerRoot();
        var killer = new FakeKiller();
        killer.Alive.Add(4242);
        killer.Alive.Add(777);
        killer.StartTicks[4242] = 999L;
        var store = new FakeMarkerStore(Marker(processGroupId: 4242, leaderStartTicks: 999L, jailPath: jail, ownerProcessId: 777));

        await CreateReaper(store, killer).StartAsync(CancellationToken.None);

        AssertEx.Empty(killer.Killed);
        AssertEx.True(Directory.Exists(jail), "a live worker's jail must survive");
        AssertEx.NotEmpty(store.Remaining, "a live worker's marker must be retained");
    }

    [Test]
    public async Task SandboxOrphanReaper_WhenThePidWasRecycled_RefusesToSignalTheGroup()
    {
        // Gate 2. Between the crash and this start the kernel may have handed the recorded pid to something unrelated.
        // The start time is what distinguishes "our orphan" from "a stranger", and a mismatch must never be signalled.
        var jail = CreateJailUnderContainerRoot();
        var killer = new FakeKiller();
        killer.StartTicks[4242] = 555L; // A different process now holds the pid.
        var store = new FakeMarkerStore(Marker(processGroupId: 4242, leaderStartTicks: 999L, jailPath: jail));

        await CreateReaper(store, killer).StartAsync(CancellationToken.None);

        AssertEx.Empty(killer.Killed, "a recycled pid must not be killed");
        // The jail is still ours to clean up, and the stale marker is discarded.
        AssertEx.False(Directory.Exists(jail));
        AssertEx.Empty(store.Remaining);
    }

    [Test]
    public async Task SandboxOrphanReaper_WhenTheLeaderIsAlreadyGone_ReapsNothingButStillCleansUp()
    {
        var jail = CreateJailUnderContainerRoot();
        var killer = new FakeKiller(); // No start ticks recorded => the process no longer exists.
        var store = new FakeMarkerStore(Marker(processGroupId: 4242, leaderStartTicks: 999L, jailPath: jail));

        await CreateReaper(store, killer).StartAsync(CancellationToken.None);

        AssertEx.Empty(killer.Killed);
        AssertEx.False(Directory.Exists(jail));
        AssertEx.Empty(store.Remaining);
    }

    [Test]
    public async Task SandboxOrphanReaper_WhenTheJailIsOutsideOurContainerRoot_ReapsTheGroupButDeletesNothing()
    {
        // Gate 3, strict path ownership — parity with StaleLlamaServerReaper.IsUnderRoot. A marker is untrusted input
        // after a crash, so a path outside our root is never a deletion target no matter what the marker claims.
        var outside = Path.Combine(Path.GetTempPath(), "xe-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        _tempPaths.Add(outside);

        var killer = new FakeKiller();
        killer.StartTicks[4242] = 999L;
        var store = new FakeMarkerStore(Marker(processGroupId: 4242, leaderStartTicks: 999L, jailPath: outside));

        await CreateReaper(store, killer).StartAsync(CancellationToken.None);

        AssertEx.Contains(killer.Killed, 4242);
        AssertEx.True(Directory.Exists(outside), "a path outside the sandbox container root must never be deleted");
    }

    [Test]
    public async Task SandboxOrphanReaper_WhenTheJailIsAPreservedWorkspace_ReapsTheGroupButKeepsTheDirectory()
    {
        // An engine-managed trusted host workspace survives kill and restart by contract — it is the user's own
        // checkout. The provider never deletes one, and neither may the reaper.
        var jail = CreateJailUnderContainerRoot();
        var killer = new FakeKiller();
        killer.StartTicks[4242] = 999L;
        var store = new FakeMarkerStore(Marker(processGroupId: 4242, leaderStartTicks: 999L, jailPath: jail, preserveJail: true));

        await CreateReaper(store, killer).StartAsync(CancellationToken.None);

        AssertEx.Contains(killer.Killed, 4242);
        AssertEx.True(Directory.Exists(jail), "a preserved workspace must never be deleted");
    }

    [Test]
    public async Task SandboxOrphanReaper_WhenTheStoreThrows_DoesNotBlockApplicationStart()
    {
        // The reaper is a startup hosted service. A failure here must degrade to "no reaping", never to a failed boot.
        var reaper = new SandboxOrphanReaper(new ThrowingMarkerStore(), new FakeKiller(), NullLogger<SandboxOrphanReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);
        await reaper.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SandboxOrphanReaper_WhenThereAreNoMarkers_DoesNothing()
    {
        var killer = new FakeKiller();

        await CreateReaper(new FakeMarkerStore(), killer).StartAsync(CancellationToken.None);

        AssertEx.Empty(killer.Killed);
    }

    [Test]
    public async Task Reap_KillsTheScopeOfADeadOwner_AndTheUnreferencedScopesAPreviousRunLeft()
    {
        // The transient scope is the reapable handle for an isolated command: its processes are in their own PID
        // namespace, so the recorded pid identifies only the outermost helper and signalling that group reaches
        // nothing the workload started.
        var deadOwnerUnit = SandboxScopeUnit.Create("compute");
        var leftoverUnit = SandboxScopeUnit.Create("compute");
        var killer = new FakeKiller();
        var scopeKiller = new FakeScopeKiller([leftoverUnit]);
        var store = new FakeMarkerStore(Marker(pgid: 4242, ownerProcessId: 9999, deadOwnerUnit));

        await new SandboxOrphanReaper(store, killer, NullLogger<SandboxOrphanReaper>.Instance, containmentProbe: null, scopeKiller)
            .StartAsync(CancellationToken.None);

        AssertEx.Contains(scopeKiller.Killed, deadOwnerUnit);
        AssertEx.Contains(scopeKiller.Killed, leftoverUnit);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Reap_LeavesTheScopeOfALiveWorkerAlone_EvenThoughItMatchesTheSweepPattern()
    {
        // The one scope that legitimately exists at startup belongs to a SECOND worker instance that is still running.
        // Without this the sweep would be a restart that kills another instance's in-flight command.
        var liveUnit = SandboxScopeUnit.Create("compute");
        var killer = new FakeKiller();
        killer.Alive.Add(4321);
        var scopeKiller = new FakeScopeKiller([liveUnit]);
        var store = new FakeMarkerStore(Marker(pgid: 4242, ownerProcessId: 4321, liveUnit));

        await new SandboxOrphanReaper(store, killer, NullLogger<SandboxOrphanReaper>.Instance, containmentProbe: null, scopeKiller)
            .StartAsync(CancellationToken.None);

        AssertEx.Empty(scopeKiller.Killed);
        AssertEx.Empty(killer.Killed);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Reap_WhenThereAreNoMarkersAtAll_StillSweepsTheScopesAPreviousRunLeft()
    {
        // The crash window the scope sweep exists for is exactly the one with no marker: `systemd-run` has created the
        // scope and the workload is running inside it, but the worker died before the marker reached disk. Returning
        // early on an empty marker set skipped the sweep in precisely that case, leaving the isolated workload running
        // until its RuntimeMaxSec — the failure mode the sweep was added to close.
        var leftoverUnit = SandboxScopeUnit.Create("compute");
        var killer = new FakeKiller();
        var scopeKiller = new FakeScopeKiller([leftoverUnit]);

        await new SandboxOrphanReaper(new FakeMarkerStore(), killer, NullLogger<SandboxOrphanReaper>.Instance, containmentProbe: null, scopeKiller)
            .StartAsync(CancellationToken.None);

        AssertEx.Contains(scopeKiller.Killed, leftoverUnit);
    }

    [Test]
    public async Task Reap_LeavesAScopeAliveWorkerHasOnlyJustRegistered_Alone()
    {
        // THE RACE the pre-registered marker closes. `systemd-run` creates the scope as its first act, so between the
        // launch and a marker written afterwards there is a window in which a live worker's brand-new scope is on the
        // manager with nothing claiming it. A second worker starting inside that window used to list it, find no
        // marker, and SIGKILL a command that had just started running. The provider now registers the marker — pid
        // still unknown — before the launch, so the sweep sees the claim from the first instant the unit can exist.
        var launchingUnit = SandboxScopeUnit.Create("compute");
        var killer = new FakeKiller();
        killer.Alive.Add(4321);
        // Old enough that only the marker can save it: the grace window is not what is being tested here.
        var scopeKiller = new FakeScopeKiller([new SandboxScopeUnitStatus(launchingUnit, TimeSpan.FromHours(1))]);
        var store = new FakeMarkerStore(PendingMarker(ownerProcessId: 4321, launchingUnit));

        await new SandboxOrphanReaper(store, killer, NullLogger<SandboxOrphanReaper>.Instance, containmentProbe: null, scopeKiller)
            .StartAsync(CancellationToken.None);

        AssertEx.Empty(scopeKiller.Killed, "a scope a live worker has registered but not yet launched into must never be signalled");
        AssertEx.Empty(killer.Killed);
        AssertEx.NotEmpty(store.Remaining, "a live worker's pending marker must be retained");
    }

    [Test]
    public async Task Reap_WhenAPendingMarkersOwnerIsDead_KillsTheScopeAndSignalsNoGroupAtAll()
    {
        // The other half of the pending state: the worker died between registering the marker and recording the pid.
        // The scope is the reapable handle and is emptied; the marker's ABSENT pid must not be turned into a target —
        // kill(-0) would signal the reaper's own process group.
        var abandonedUnit = SandboxScopeUnit.Create("compute");
        var killer = new FakeKiller();
        var scopeKiller = new FakeScopeKiller([abandonedUnit]);
        var store = new FakeMarkerStore(PendingMarker(ownerProcessId: 9999, abandonedUnit));

        await new SandboxOrphanReaper(store, killer, NullLogger<SandboxOrphanReaper>.Instance, containmentProbe: null, scopeKiller)
            .StartAsync(CancellationToken.None);

        AssertEx.Contains(scopeKiller.Killed, abandonedUnit);
        AssertEx.Empty(killer.Killed, "a marker with no recorded pid must never signal a process group");
        AssertEx.Empty(store.Remaining);
    }

    [Test]
    public async Task Reap_WhenAMarkerNamesPgidZero_RefusesToSignalIt()
    {
        // Defence against a marker from an older build or a torn write. To kill(2) a pgid of 0 means "my own process
        // group": signalling it would have the reaper kill the worker it is starting.
        var killer = new FakeKiller();
        killer.StartTicks[0] = 1L;
        var store = new FakeMarkerStore(Marker(pgid: 0, ownerProcessId: 9999, scopeUnitName: null));

        await CreateReaper(store, killer).StartAsync(CancellationToken.None);

        AssertEx.Empty(killer.Killed, "pgid 0 is the reaper's own process group");
    }

    [Test]
    public async Task Reap_WhenAnUnreferencedScopeIsYoungerThanTheGrace_LeavesItForTheNextStart()
    {
        // Defence in depth behind the marker, for the worker whose marker store is unwritable: its live scopes are
        // indistinguishable on disk from a previous run's leftovers, and only their age separates them. The two
        // mistakes are not symmetric — signalling a live command destroys work, skipping an orphan costs it one
        // RuntimeMaxSec — so a young unreferenced scope is left alone.
        var youngUnit = SandboxScopeUnit.Create("compute");
        var killer = new FakeKiller();
        var scopeKiller = new FakeScopeKiller([new SandboxScopeUnitStatus(youngUnit, TimeSpan.FromSeconds(5))]);

        await new SandboxOrphanReaper(new FakeMarkerStore(), killer, NullLogger<SandboxOrphanReaper>.Instance, containmentProbe: null, scopeKiller)
            .StartAsync(CancellationToken.None);

        AssertEx.Empty(scopeKiller.Killed, "a scope younger than the grace window may still belong to a worker starting alongside this one");
    }

    [Test]
    public async Task Reap_WhenAnUnreferencedScopesAgeIsUnknown_LeavesItAlone()
    {
        // An age the user manager did not report is not an age of zero and not an age of infinity. Signalling on an
        // unmeasured unit would make a parsing failure lethal, so the sweep fails towards leaving processes running.
        var unmeasuredUnit = SandboxScopeUnit.Create("compute");
        var killer = new FakeKiller();
        var scopeKiller = new FakeScopeKiller([new SandboxScopeUnitStatus(unmeasuredUnit, ActiveFor: null)]);

        await new SandboxOrphanReaper(new FakeMarkerStore(), killer, NullLogger<SandboxOrphanReaper>.Instance, containmentProbe: null, scopeKiller)
            .StartAsync(CancellationToken.None);

        AssertEx.Empty(scopeKiller.Killed, "an unmeasurable age must not authorise an irreversible signal");
    }

    // ---- helpers ----

    private static SandboxProcessMarker Marker(int? pgid, int ownerProcessId, string? scopeUnitName)
    {
        return new SandboxProcessMarker
        {
            SandboxId = "sandbox",
            ProcessGroupId = pgid,
            LeaderStartTicks = pgid is null ? null : 1,
            JailPath = Path.Combine(SandboxPaths.ContainerRoot, "absent"),
            OwnerProcessId = ownerProcessId,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ScopeUnitName = scopeUnitName
        };
    }

    /// <summary>
    ///     The marker the provider writes BEFORE it launches an isolated command: it names the scope the launch is
    ///     about to create and carries no pid, because there is no child yet.
    /// </summary>
    private static SandboxProcessMarker PendingMarker(int ownerProcessId, string scopeUnitName)
    {
        return Marker(pgid: null, ownerProcessId, scopeUnitName);
    }

    private static SandboxOrphanReaper CreateReaper(ISandboxMarkerStore store, ISandboxProcessGroupKiller killer)
    {
        return new SandboxOrphanReaper(store, killer, NullLogger<SandboxOrphanReaper>.Instance);
    }

    private string CreateJailUnderContainerRoot()
    {
        // Real directory under the real container root, so the ownership check is exercised against the production path
        // rather than a stand-in.
        var jail = Path.Combine(SandboxPaths.ContainerRoot, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jail);
        File.WriteAllText(Path.Combine(jail, "artifact.txt"), "content");
        _tempPaths.Add(jail);
        return jail;
    }

    private static SandboxProcessMarker Marker(int processGroupId,
        long leaderStartTicks,
        string jailPath,
        int ownerProcessId = 999_999,
        bool preserveJail = false)
    {
        return new SandboxProcessMarker
        {
            SandboxId = "process-node-abc",
            ProcessGroupId = processGroupId,
            LeaderStartTicks = leaderStartTicks,
            JailPath = jailPath,
            PreserveJail = preserveJail,
            OwnerProcessId = ownerProcessId,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private sealed class FakeKiller : ISandboxProcessGroupKiller
    {
        public HashSet<int> Alive { get; } = [];

        public Dictionary<int, long> StartTicks { get; } = [];

        public List<int> Killed { get; } = [];

        public long? GetProcessStartTicks(int processId)
        {
            return StartTicks.TryGetValue(processId, out var ticks) ? ticks : null;
        }

        public bool IsProcessAlive(int processId)
        {
            return Alive.Contains(processId);
        }

        public Task KillProcessGroupAsync(int processGroupId, CancellationToken cancellationToken = default)
        {
            Killed.Add(processGroupId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScopeKiller : ISandboxScopeUnitKiller
    {
        private readonly IReadOnlyList<SandboxScopeUnitStatus> _loaded;

        // The default age is well past the sweep's grace, so a test that says nothing about age is asking about
        // ownership; the tests that are about the grace state their own.
        public FakeScopeKiller(IReadOnlyList<string> loaded)
            : this([.. loaded.Select(unit => new SandboxScopeUnitStatus(unit, TimeSpan.FromHours(1)))])
        {
        }

        public FakeScopeKiller(IReadOnlyList<SandboxScopeUnitStatus> loaded)
        {
            _loaded = loaded;
        }

        public List<string> Killed { get; } = [];

        public Task KillAsync(string unitName, CancellationToken cancellationToken = default)
        {
            Killed.Add(unitName);

            return Task.CompletedTask;
        }

        public IReadOnlyList<SandboxScopeUnitStatus> ListEngineOwnedUnits()
        {
            return _loaded;
        }
    }

    private sealed class FakeMarkerStore : ISandboxMarkerStore
    {
        private readonly Dictionary<string, SandboxProcessMarker> _markers = [];

        public FakeMarkerStore(params SandboxProcessMarker[] markers)
        {
            for (var index = 0; index < markers.Length; index++)
            {
                _markers["marker-" + index.ToString(CultureInfo.InvariantCulture)] = markers[index];
            }
        }

        public IReadOnlyCollection<string> Remaining => _markers.Keys;

        public string? Write(SandboxProcessMarker marker)
        {
            var id = "marker-" + Guid.NewGuid().ToString("N");
            _markers[id] = marker;
            return id;
        }

        public void Update(string markerId, SandboxProcessMarker marker)
        {
            _markers[markerId] = marker;
        }

        public void Delete(string markerId)
        {
            _ = _markers.Remove(markerId);
        }

        public IReadOnlyList<SandboxMarkerEntry> ReadAll()
        {
            return [.. _markers.Select(entry => new SandboxMarkerEntry(entry.Key, entry.Value))];
        }
    }

    private sealed class ThrowingMarkerStore : ISandboxMarkerStore
    {
        public string? Write(SandboxProcessMarker marker)
        {
            return null;
        }

        public void Update(string markerId, SandboxProcessMarker marker)
        {
            // Not reached.
        }

        public void Delete(string markerId)
        {
            // Not reached.
        }

        public IReadOnlyList<SandboxMarkerEntry> ReadAll()
        {
            throw new IOException("the marker directory is unreadable");
        }
    }
}
