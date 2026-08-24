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

    // ---- helpers ----

    private static SandboxProcessMarker Marker(int pgid, int ownerProcessId, string? scopeUnitName)
    {
        return new SandboxProcessMarker
        {
            SandboxId = "sandbox",
            ProcessGroupId = pgid,
            LeaderStartTicks = 1,
            JailPath = Path.Combine(SandboxPaths.ContainerRoot, "absent"),
            OwnerProcessId = ownerProcessId,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ScopeUnitName = scopeUnitName
        };
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
        private readonly IReadOnlyList<string> _loaded;

        public FakeScopeKiller(IReadOnlyList<string> loaded)
        {
            _loaded = loaded;
        }

        public List<string> Killed { get; } = [];

        public Task KillAsync(string unitName, CancellationToken cancellationToken = default)
        {
            Killed.Add(unitName);

            return Task.CompletedTask;
        }

        public IReadOnlyList<string> ListEngineOwnedUnits()
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
