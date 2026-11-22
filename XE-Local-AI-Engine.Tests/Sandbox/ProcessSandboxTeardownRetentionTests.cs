namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The provider's one shared teardown of a real <c>setsid</c> child: an unverified group kill keeps the orphan marker on every live
///     path, every caller shares one kill, and a member that outlives its leader is killed through the pin taken at launch.
/// </summary>
/// <remarks>The launches are setsid-only (no scope unit), so a scope kill can never mask what the group kill did.</remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
[UnsupportedOSPlatform("windows")]
public sealed class ProcessSandboxTeardownRetentionTests
{
    [Test]
    public async Task Execute_WhenTheGroupKillIsDeferred_KeepsTheMarkerItRecorded()
    {
        using var store = new GatedMarkerStore();
        var liveAtKill = -1;
        var killer = new FakeOwnedGroupKiller
        {
            Outcome = SandboxGroupKillOutcome.Deferred,
            DuringOwnedKill = () => liveAtKill = store.Live.Count
        };
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);

        // The child reads stdin, which the provider writes only after the pin and the marker, so it is alive for both.
        var result = await provider.ExecuteAsync(handle, Command("read x", standardInput: "go\n"));

        AssertEx.True(result.Completed, result.StandardError);
        AssertEx.Equal(expected: 1, liveAtKill, "the marker existed before the teardown ran");
        AssertEx.Equal(expected: 1, killer.OwnedKills);
        AssertEx.Equal(expected: 1, store.Live.Count);
        AssertEx.True(store.Live[0].ProcessGroupId > 0 && store.Live[0].LeaderStartTicks == 99, "the kept marker carries the identity snapshot");
    }

    [Test]
    public async Task Execute_WhenItTimesOut_AndTheGroupKillIsDeferred_KillsOnce_AndKeepsTheMarker()
    {
        using var store = new GatedMarkerStore();
        var killer = new FakeOwnedGroupKiller
        {
            Outcome = SandboxGroupKillOutcome.Deferred
        };
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);

        // real-timer: the subject is a real OS process, and the timeout is the subject's own input.
        var result = await provider.ExecuteAsync(handle, Command("exec sleep 30", timeout: TimeSpan.FromMilliseconds(500)));

        AssertEx.False(result.Completed);
        AssertEx.Equal(expected: 1, killer.OwnedKills, "the timeout catch and the finally share one kill");
        AssertEx.Equal(expected: 1, store.Live.Count);
    }

    [Test]
    public async Task KillAsync_WhileTheCommandRuns_KillsOnce_AndTheCommandKeepsItsMarker()
    {
        using var store = new GatedMarkerStore();
        var killer = new FakeOwnedGroupKiller
        {
            Outcome = SandboxGroupKillOutcome.Deferred
        };
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);

        var execution = provider.ExecuteAsync(handle, Command("exec sleep 30"));
        _ = await store.Completed.Task.WaitAsync(TestBudgets.Contended);
        await provider.KillAsync(handle);
        var result = await execution.WaitAsync(TestBudgets.Contended);

        AssertEx.False(result.Completed, "a sandbox kill cancels the command");
        AssertEx.Equal(expected: 1, killer.OwnedKills, "the registry's kill and the command's own teardown are one kill");
        AssertEx.Equal(expected: 1, store.Live.Count, "the registry's Deferred outcome reaches the command's marker decision");
    }

    [Test]
    public async Task Dispose_WhileTheCommandsTeardownIsClassifying_WaitsForIt_KillsOnce_AndKeepsTheMarker()
    {
        using var store = new GatedMarkerStore();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var killer = new FakeOwnedGroupKiller
        {
            Outcome = SandboxGroupKillOutcome.Deferred,
            DuringOwnedKill = () =>
            {
                entered.Set();
                _ = release.Wait(TestBudgets.Contended);
            }
        };
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);

        // real-timer: the subject is a real OS process, and the timeout is the subject's own input.
        var execution = provider.ExecuteAsync(handle, Command("exec sleep 30", timeout: TimeSpan.FromMilliseconds(300)));
        AssertEx.True(await Task.Run(() => entered.Wait(TestBudgets.Contended)), "the command's teardown never reached the kill");
        var dispose = Task.Run(provider.Dispose);
        await AssertEx.StaysIncompleteAsync(dispose, "disposal must wait for the kill in progress, not start a second one");
        release.Set();

        await dispose.WaitAsync(TestBudgets.Contended);
        _ = await execution.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(expected: 1, killer.OwnedKills);
        AssertEx.Equal(expected: 1, store.Live.Count);
    }

    [Test]
    public async Task Dispose_AfterTheCommandClassifiedItsTeardown_KillsNothingMore_AndKeepsTheMarker()
    {
        using var store = new GatedMarkerStore();
        var killer = new FakeOwnedGroupKiller
        {
            Outcome = SandboxGroupKillOutcome.Deferred
        };
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);
        _ = await provider.ExecuteAsync(handle, Command("read x", standardInput: "go\n"));

        provider.Dispose();

        AssertEx.Equal(expected: 1, killer.OwnedKills);
        AssertEx.Equal(expected: 1, store.Live.Count);
    }

    [Test]
    public async Task Execute_WhenTheSandboxIsKilledWhileTheChildStarts_KeepsTheMarkerOfADeferredKill()
    {
        using var store = new GatedMarkerStore
        {
            GateCompletion = true
        };
        var killer = new FakeOwnedGroupKiller
        {
            Outcome = SandboxGroupKillOutcome.Deferred
        };
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);

        // On a pool thread: the plain launch reaches the gate synchronously, and it blocks the thread that runs into it.
        var execution = Task.Run(() => provider.ExecuteAsync(handle, Command("exec sleep 30")));
        await store.CompletionEntered.Task.WaitAsync(TestBudgets.Contended);
        await provider.KillAsync(handle);
        store.ReleaseCompletion.Set();
        var result = await execution.WaitAsync(TestBudgets.Contended);

        AssertEx.Contains(result.StandardError, "killed before the command started");
        AssertEx.Equal(expected: 1, killer.OwnedKills);
        AssertEx.Equal(expected: 1, store.Live.Count);
    }

    [Test]
    public async Task StartInteractive_WhenTheSandboxIsKilledWhileTheChildStarts_KeepsTheMarkerOfADeferredKill()
    {
        using var store = new GatedMarkerStore
        {
            GateCompletion = true
        };
        var killer = new FakeOwnedGroupKiller
        {
            Outcome = SandboxGroupKillOutcome.Deferred
        };
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);

        // On a pool thread: the plain launch reaches the gate synchronously, and it blocks the thread that runs into it.
        var start = Task.Run(() => provider.StartInteractiveAsync(handle, Command("exec sleep 30")));
        await store.CompletionEntered.Task.WaitAsync(TestBudgets.Contended);
        await provider.KillAsync(handle);
        store.ReleaseCompletion.Set();

        _ = await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => start.WaitAsync(TestBudgets.Contended));
        AssertEx.Equal(expected: 1, killer.OwnedKills);
        AssertEx.Equal(expected: 1, store.Live.Count);
    }

    [Test]
    public async Task InteractiveDispose_WhenTheGroupKillIsDeferred_KeepsTheMarker_AndASecondDisposeKillsNothing()
    {
        using var store = new GatedMarkerStore();
        var killer = new FakeOwnedGroupKiller
        {
            Outcome = SandboxGroupKillOutcome.Deferred
        };
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);
        var interactive = await provider.StartInteractiveAsync(handle, Command("exec sleep 30"));
        AssertEx.Equal(expected: 1, store.Live.Count, "the marker exists before the teardown");

        await interactive.DisposeAsync();
        await interactive.DisposeAsync();

        AssertEx.Equal(expected: 1, killer.OwnedKills);
        AssertEx.Equal(expected: 1, store.Live.Count);
    }

    /// <summary>The leader exits first; its backgrounded member is killed through the pin after the leader was reaped.</summary>
    [Test]
    public async Task Execute_WhenTheLeaderExitsFirst_KillsItsSurvivingMemberThroughThePin()
    {
        SkipUnlessGroupFlag(expectSupported: true);
        using var store = new GatedMarkerStore();
        using var killer = new ObservingKiller();
        try
        {
            await RunLeaderThatLeavesAMemberAsync(store, killer);

            var member = AssertEx.NotNull(killer.MemberPin, "the member was pinned before the kill");
            AssertEx.True(killer.LeaderExitedAtKill, "the leader had exited and been reaped before the group kill");
            AssertEx.True(killer.MemberAliveAtKill, "the member was alive when the group kill ran");
            AssertEx.Equal(SandboxGroupKillOutcome.Killed, killer.Outcome);
            await AssertEx.EventuallyAsync(() => member.Send(0, group: false).Status == LinuxPidFdSendStatus.Gone, TestBudgets.Contended,
                "the member outlived its group kill");
            AssertEx.Empty(store.Live, "a verified kill releases the marker");
        }
        finally
        {
            killer.KillMember();
        }
    }

    /// <summary>Without the group flag the leader's death forbids any numeric group signal: the member is only probed and the marker kept.</summary>
    [Test]
    public async Task Execute_WithoutTheGroupFlag_WhenTheLeaderExitsFirst_KeepsTheMarker_AndSignalsNothing()
    {
        SkipUnlessGroupFlag(expectSupported: false);
        using var store = new GatedMarkerStore();
        using var killer = new ObservingKiller();
        try
        {
            await RunLeaderThatLeavesAMemberAsync(store, killer);

            var member = AssertEx.NotNull(killer.MemberPin, "the member was pinned before the kill");
            AssertEx.Equal(SandboxGroupKillOutcome.Deferred, killer.Outcome);
            AssertEx.Equal(LinuxPidFdSendStatus.Sent, member.Send(0, group: false).Status, "no signal may follow the leader's death");
            AssertEx.Equal(expected: 1, store.Live.Count);
        }
        finally
        {
            killer.KillMember();
        }
    }

    private static async Task RunLeaderThatLeavesAMemberAsync(GatedMarkerStore store, ObservingKiller killer)
    {
        using var provider = CreateProvider(store, killer);
        var handle = await CreateSandboxAsync(provider);

        // The member's stdio is closed so the provider's output drain ends with the leader; the leader leaves after the pin.
        var result = await provider.ExecuteAsync(handle,
            Command($"(exec sleep 600) </dev/null >/dev/null 2>&1 & echo $! > '{killer.MemberPidFile}'; read x", standardInput: "go\n"));
        File.Delete(killer.MemberPidFile);
        AssertEx.True(result.Completed, result.StandardError);
    }

    private static void SkipUnlessGroupFlag(bool expectSupported)
    {
        using var self = LinuxPidFd.Open(Environment.ProcessId);
        if (self.OpenStatus != LinuxPidFdOpenStatus.Opened)
        {
            Skip.Test($"BLOCKED: pidfd_open is {self.OpenStatus} here, so no pin can be taken.");
        }

        var supported = self.Send(0, group: true).Status != LinuxPidFdSendStatus.GroupFlagUnsupported;
        if (supported != expectSupported)
        {
            Skip.Test(expectSupported
                ? "BLOCKED: this kernel has no pidfd group signal (before 6.9); the old-kernel counterpart covers it."
                : "BLOCKED: this kernel has the pidfd group signal; this counterpart covers kernels before 6.9.");
        }
    }

    private static SandboxCommandRequest Command(string script, string? standardInput = null, TimeSpan? timeout = null) =>
        new()
        {
            ExecutionId = "teardown-" + Guid.NewGuid().ToString("N"),
            Executable = "/bin/sh",
            Arguments = ["-c", script],
            StandardInput = standardInput,
            Timeout = timeout ?? TimeSpan.FromSeconds(60)
        };

    private static ProcessSandboxRuntimeProvider CreateProvider(GatedMarkerStore store, ISandboxProcessGroupKiller killer)
    {
        var setsid = new HostSandboxContainmentProbe().Containment.SetsidPath;
        if (setsid is null)
        {
            Skip.Test("BLOCKED: this host has no setsid, so no child leads its own group.");
        }

        var containment = new SandboxContainment
        {
            SupportsProcessGroup = true,
            SetsidPath = setsid
        };
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(new FixedProbe(containment)),
            store,
            groupKiller: killer);
    }

    private static Task<SandboxHandle> CreateSandboxAsync(ProcessSandboxRuntimeProvider provider) =>
        provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner-teardown",
                NodeId = "node-" + Guid.NewGuid().ToString("N"),
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "dotnet-agent-home",
                ManifestVersion = AgentHomeManifest.CurrentVersion
            },
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted
        });

    private sealed class FixedProbe : ISandboxContainmentProbe
    {
        public FixedProbe(SandboxContainment containment)
        {
            Containment = containment;
        }

        public SandboxContainment Containment { get; }
    }

    /// <summary>The real killer, recording what held when the owned kill ran, and pinning the member first so cleanup never needs its number.</summary>
    private sealed class ObservingKiller : ISandboxProcessGroupKiller, IDisposable
    {
        private readonly LinuxSandboxProcessGroupKiller _inner = new(TimeProvider.System);

        public string MemberPidFile { get; } = Path.Combine(Path.GetTempPath(), $"xe-teardown-member-{Guid.NewGuid():N}.pid");

        /// <summary>The member, pinned while it was alive, before the kill; the test's own cleanup signals only through it.</summary>
        public LinuxPidFd? MemberPin { get; private set; }

        public bool LeaderExitedAtKill { get; private set; }

        public bool MemberAliveAtKill { get; private set; }

        public SandboxGroupKillOutcome? Outcome { get; private set; }

        public long? GetProcessStartTicks(int processId) =>
            _inner.GetProcessStartTicks(processId);

        public bool IsProcessAlive(int processId) =>
            _inner.IsProcessAlive(processId);

        public Task<SandboxGroupKillOutcome> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken = default) =>
            _inner.KillProcessGroupAsync(processGroupId, expectedStartTicks, cancellationToken);

        public LinuxPidFd PinProcess(int processId) =>
            _inner.PinProcess(processId);

        public SandboxGroupKillOutcome KillOwnedProcessGroup(LinuxPidFd? leaderPin,
            LinuxPidFdOpenStatus pinStatus,
            int processGroupId,
            Func<bool> leaderExited,
            Func<bool> killTree)
        {
            LeaderExitedAtKill = leaderExited();
            var member = int.Parse(File.ReadAllText(MemberPidFile).Trim(), CultureInfo.InvariantCulture);
            MemberPin = LinuxPidFd.Open(member);
            MemberAliveAtKill = MemberPin.OpenStatus == LinuxPidFdOpenStatus.Opened
                                && LinuxProcStat.Read(string.Create(CultureInfo.InvariantCulture, $"/proc/{member}/stat")) is { Outcome: LinuxProcStatOutcome.Parsed, Exited: false };
            Outcome = _inner.KillOwnedProcessGroup(leaderPin, pinStatus, processGroupId, leaderExited, killTree);
            return Outcome.Value;
        }

        /// <summary>Test cleanup, independent of the code under test: SIGKILL through the member's own pin, which names it or nothing.</summary>
        public void KillMember()
        {
            if (MemberPin is { } pin)
            {
                _ = pin.Send(9, group: false);
            }
        }

        public void Dispose()
        {
            MemberPin?.Dispose();
            File.Delete(MemberPidFile);
        }
    }
}
