namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>ImageServerProcessSupervisor.GetResidents</c>: the process table read from memory, one entry per registered
///     daemon, with its job lease and an exited-but-unreaped child reported as they are.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ImageServerSupervisorResidentsTests
{
    [Test]
    public async Task GetResidents_WithNoDaemon_IsEmpty()
    {
        await using var supervisor = ImageSupervisorFactory.Create();

        AssertEx.Equal(expected: 0, supervisor.GetResidents().Count);
    }

    [Test]
    public async Task GetResidents_ListsAResidentDaemonAsIdleAndLive()
    {
        var clock = new AdvanceableClock();
        await using var supervisor = ImageSupervisorFactory.Create(timeProvider: clock);
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);

        var resident = supervisor.GetResidents().Single();

        AssertEx.Equal("sd15", resident.ModelName);
        AssertEx.False(resident.HasActiveJobLease);
        AssertEx.False(resident.HasExited);
        AssertEx.Equal(clock.GetUtcNow(), resident.LastUsedUtc);
    }

    [Test]
    public async Task GetResidents_ReportsAJobLeaseUntilItIsDisposed()
    {
        await using var supervisor = ImageSupervisorFactory.Create();
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);

        var lease = AssertEx.NotNull(supervisor.TryAcquireJobLease("sd15"));
        AssertEx.True(supervisor.GetResidents().Single().HasActiveJobLease);

        lease.Dispose();
        AssertEx.False(supervisor.GetResidents().Single().HasActiveJobLease);
    }

    [Test]
    public async Task GetResidents_ReportsAnExitedDaemonThatIsNotReapedYet()
    {
        var launcher = new FakeImageProcessLauncher();
        await using var supervisor = ImageSupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);

        launcher.Handles.Single().SimulateExit(exitCode: 137);

        var resident = supervisor.GetResidents().Single();
        AssertEx.Equal("sd15", resident.ModelName);
        AssertEx.True(resident.HasExited);
    }
}
