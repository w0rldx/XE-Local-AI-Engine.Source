namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>The one shared kill of a launched sandbox child: pinned identity at launch, group before leader, once for every caller.</summary>
[Category(TestCategories.Unit)]
public sealed class SandboxLaunchTeardownTests
{
    [Test]
    [RunOn(OS.Linux)]
    public async Task Create_PinsThenReadsTicksThenRechecksTheExit_AndOffersTheValidatedIdentity()
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        using var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: true, new FakeOwnedGroupKiller(calls));

        AssertEx.Equal("pin 4242,ticks 4242", string.Join(',', calls));
        AssertEx.Equal(4242, teardown.MarkerProcessGroupId);
        AssertEx.Equal(99L, teardown.MarkerStartTicks);
        await Task.CompletedTask;
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Create_WhenTheChildExitsBetweenTheOpenAndTheStatRead_RecordsNoIdentityAndDropsThePin()
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        // The pid may have been reaped and reused before the ticks were read: neither the pin nor the ticks can be trusted.
        var killer = new FakeOwnedGroupKiller(calls)
        {
            AfterTicksRead = () => child.ExitCode = 0
        };
        using var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: true, killer);

        AssertEx.Null(teardown.MarkerProcessGroupId, "a marker must not record an identity read across an exit");
        AssertEx.Null(teardown.MarkerStartTicks);
        AssertEx.Equal(SandboxGroupKillOutcome.Killed, teardown.Terminate());
        AssertEx.Contains(calls, "group 4242 Gone unpinned");
        AssertEx.True(killer.Pins[0].IsClosed, "the dropped pin is closed at once");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Terminate_HandsTheGroupKillThePin_AndTheLeaderKillAsItsTree()
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        var killer = new FakeOwnedGroupKiller(calls);
        using var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: true, killer);
        calls.Clear();

        var outcome = teardown.Terminate();

        AssertEx.Equal(SandboxGroupKillOutcome.Killed, outcome);
        AssertEx.Equal("group 4242 Opened,tree", string.Join(',', calls));
        AssertEx.True(ReferenceEquals(killer.Pins[0], killer.LastPin), "the kill goes through the pin taken at launch");
        await Task.CompletedTask;
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Terminate_Twice_KillsOnce_AndReturnsTheSameOutcome()
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        var killer = new FakeOwnedGroupKiller(calls)
        {
            Outcome = SandboxGroupKillOutcome.Deferred
        };
        using var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: true, killer);

        var first = teardown.Terminate();
        var second = teardown.Terminate();

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, first);
        AssertEx.Equal(first, second);
        AssertEx.Equal(expected: 1, killer.OwnedKills);
        AssertEx.Equal(expected: 1, child.KillCount);
        await Task.CompletedTask;
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Terminate_ConcurrentCallers_KillOnce_AndTheLateCallerReadsTheFirstOutcome()
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var killer = new FakeOwnedGroupKiller(calls)
        {
            Outcome = SandboxGroupKillOutcome.Deferred,
            DuringOwnedKill = () =>
            {
                entered.Set();
                _ = release.Wait(TestBudgets.Contended);
            }
        };
        using var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: true, killer);

        var first = Task.Run(teardown.Terminate);
        AssertEx.True(entered.Wait(TestBudgets.Contended), "the first caller never reached the kill");
        var second = Task.Run(teardown.Terminate);
        await AssertEx.StaysIncompleteAsync(second, "a second caller must wait for the kill in progress, not start its own");
        release.Set();

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, await first);
        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, await second);
        AssertEx.Equal(expected: 1, killer.OwnedKills);
    }

    /// <summary>As PID 1, or with a foreign /proc, the runtime may reap before the exit check flips: neither the pin nor the ticks prove anything.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Create_WhenTheHostCannotVerifyItsChildren_TakesNoPin_AndRecordsNoIdentity()
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        var killer = new FakeOwnedGroupKiller(calls);
        using var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: true, killer, hostVerifiable: static () => false);

        AssertEx.Empty(calls, "no pin is opened and no start time read");
        AssertEx.Null(teardown.MarkerProcessGroupId);
        AssertEx.Null(teardown.MarkerStartTicks);
        AssertEx.Equal(SandboxGroupKillOutcome.Killed, teardown.Terminate());
        AssertEx.Equal("group 4242 Unsupported unpinned,tree", string.Join(',', calls), "the kill takes the numeric fallback, unpinned");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Terminate_WithoutAProcessGroup_KillsOnlyTheLeader()
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        var killer = new FakeOwnedGroupKiller(calls);
        using var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: false, killer);

        AssertEx.Equal(SandboxGroupKillOutcome.Gone, teardown.Terminate());
        AssertEx.Equal("tree", string.Join(',', calls), "no pin, no group: an MXC, Windows job or non-setsid child is its own kill");
        AssertEx.Null(teardown.MarkerProcessGroupId);
        await Task.CompletedTask;
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Dispose_DecidesTheOutcomeBeforeClosingThePin()
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        var killer = new FakeOwnedGroupKiller(calls);
        var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: true, killer);

        teardown.Dispose();

        AssertEx.Equal(expected: 1, killer.OwnedKills, "disposal without a prior kill runs it, while the pin is open");
        AssertEx.True(killer.Pins[0].IsClosed);
        AssertEx.Equal(SandboxGroupKillOutcome.Killed, teardown.Terminate(), "a late caller reads the decided outcome");
        AssertEx.Equal(expected: 1, killer.OwnedKills);
        await Task.CompletedTask;
    }

    [Test]
    [RunOn(OS.Linux)]
    [Arguments(LinuxPidFdOpenStatus.Failed)]
    [Arguments(LinuxPidFdOpenStatus.Unsupported)]
    public async Task Create_WhenThePinCannotBeOpened_KeepsTheStatusForTheNumericFallback(LinuxPidFdOpenStatus status)
    {
        var calls = new List<string>();
        using var child = new FakeSandboxChildProcess(calls);
        using var teardown = SandboxLaunchTeardown.Create(child, appliedProcessGroup: true, new FakeOwnedGroupKiller(calls)
        {
            PinStatus = status
        });

        _ = teardown.Terminate();

        AssertEx.Contains(calls, $"group 4242 {status} unpinned");
        AssertEx.Equal(99L, teardown.MarkerStartTicks, "an unexited child's ticks are still a valid marker identity");
        await Task.CompletedTask;
    }
}
