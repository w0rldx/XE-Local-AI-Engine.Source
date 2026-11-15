namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary><c>FindRunningEndpoint</c> reads the process table only: it never launches, and answers null for a key with no live process.</summary>
[Category(TestCategories.Unit)]
public sealed class SupervisorFindRunningEndpointTests
{
    [Test]
    public async Task NothingRunning_IsNull_AndLaunchesNothing()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        AssertEx.Null(supervisor.FindRunningEndpoint("model-a", ModelRole.Chat));
        AssertEx.Equal(expected: 0, launcher.LaunchCount);
    }

    [Test]
    public async Task ARunningProcess_IsFoundUnderItsOwnRoleOnly_WithoutAnotherLaunch()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        var ensured = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(ensured, supervisor.FindRunningEndpoint("model-a", ModelRole.Chat));
        AssertEx.Null(supervisor.FindRunningEndpoint("model-a", ModelRole.Embedding));
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task AnExitedProcess_IsNull_AndIsNotRespawned()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        launcher.Handles.Single().SimulateExit();

        AssertEx.Null(supervisor.FindRunningEndpoint("model-a", ModelRole.Chat));
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }
}
