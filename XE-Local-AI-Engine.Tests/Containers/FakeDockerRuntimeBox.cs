namespace XE_Local_AI_Engine.Tests.Containers;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Testing.FakeDocker;
using XE_Local_AI_Engine.Tests.ContainerSandbox;

/// <summary>
///     A real <see cref="IContainerRuntime" /> — the production wire client, not a double — pointed at a
///     <see cref="FakeDockerServer" /> of its own.
///     <para>
///         Construction goes through <c>DockerContainerRuntimeFactory</c> and <c>DockerDaemonEndpointResolver</c>,
///         the same path the real-daemon suite uses, with nothing but the endpoint changed. That is what makes these
///         tests evidence about the production client: a shortcut that built the client differently would be
///         evidence about the shortcut.
///     </para>
///     <para>
///         One server per box and one box per test. The alternative — a shared daemon with a reset — would carry a
///         container from one test into the next one's label filter, and a Kestrel start on a loopback port costs
///         less than that class of flake.
///     </para>
/// </summary>
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
                    NullLoggerFactory.Instance)
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
