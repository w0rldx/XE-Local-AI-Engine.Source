namespace XE_Local_AI_Engine.Tests.HostAgent;

using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentFakeDockerDriverTests
{
    [Test]
    public async Task FakeDockerRuntimeClient_WhenStarted_ExposesRunningDevContainers()
    {
        var docker = new FakeDockerRuntimeClient(TimeProvider.System);

        var containers = await docker.ListContainersAsync(CancellationToken.None);

        AssertEx.ContainsSingle(containers, container => container.Name == "ollama" && container.IsRunning);
        AssertEx.ContainsSingle(containers, container => container.Name == "xe-node-web-server" && container.IsRunning);
    }

    [Test]
    public async Task FakeDockerRuntimeClient_WhenStoppedAndStarted_UpdatesContainerState()
    {
        var docker = new FakeDockerRuntimeClient(TimeProvider.System);

        await docker.StopContainerAsync("ollama", TimeSpan.FromSeconds(30), CancellationToken.None);
        var stopped = await docker.ListContainersAsync(CancellationToken.None);
        AssertEx.ContainsSingle(stopped, container => container.Name == "ollama" && !container.IsRunning && container.State == "exited");

        await docker.StartContainerAsync("ollama", CancellationToken.None);
        var started = await docker.ListContainersAsync(CancellationToken.None);
        AssertEx.ContainsSingle(started, container => container.Name == "ollama" && container.IsRunning && container.State == "running");
    }
}
