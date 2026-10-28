namespace XE_Local_AI_Engine.Tests.Containers;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Testing.FakeDocker;
using XE_Local_AI_Engine.Tests.ContainerSandbox;

/// <summary>
///     A real <see cref="IContainerRuntime" /> (the production wire client, not a double) pointed at a
///     <see cref="FakeDockerServer" /> of its own, built through <c>DockerContainerRuntimeFactory</c> and
///     <c>DockerDaemonEndpointResolver</c> exactly as the real-daemon suite builds it, with only the endpoint changed.
/// </summary>
/// <remarks>
///     That makes these tests evidence about the production client, not about a shortcut. One server per box and one
///     box per test: a shared daemon with a reset would carry a container from one test into the next one's label
///     filter, and a Kestrel start on a loopback port costs less than that class of flake.
/// </remarks>
internal sealed class FakeDockerRuntimeBox : IAsyncDisposable
{
    private FakeDockerRuntimeBox(FakeDockerServer docker, IContainerRuntime runtime)
    {
        Docker = docker;
        Runtime = runtime;
    }

    /// <summary>The fake daemon. Seed images, networks and scripts on its <c>State</c>.</summary>
    public FakeDockerServer Docker { get; }

    /// <summary>The production client, talking to <see cref="Docker" /> over a real socket.</summary>
    public IContainerRuntime Runtime { get; }

    /// <summary>Everything the client sent, oldest first.</summary>
    public FakeDockerState State => Docker.State;

    public static async Task<FakeDockerRuntimeBox> StartAsync(FakeDockerOptions? options = null)
    {
        var docker = await FakeDockerServer.StartAsync(options);
        try
        {
            var runtimeOptions = new ContainerRuntimeOptions
            {
                DaemonEndpoint = docker.BaseAddress.ToString()
            };

            var runtime = new DockerContainerRuntimeFactory(new StaticOptionsMonitor<ContainerRuntimeOptions>(runtimeOptions),
                    NullLoggerFactory.Instance, TimeProvider.System)
                .CreateRuntime(DockerDaemonEndpointResolver.Resolve(runtimeOptions.DaemonEndpoint));

            return new FakeDockerRuntimeBox(docker, runtime);
        }
        catch
        {
            await docker.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        await Docker.DisposeAsync();
    }
}
