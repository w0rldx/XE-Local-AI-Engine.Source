namespace XE_Local_AI_Engine.Tests.Containers;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.ContainerSandbox;

/// <summary>
///     An <see cref="IDockerDaemonAttestationStore" /> that keeps its single pin in memory.
///     <para>
///         Every test in this folder constructs one and passes it explicitly, and that is a rule rather than a
///         convenience: the production store is one unkeyed pin in a file under the node data directory, so a test
///         that used it would pin the developer's real daemon and race the Development Mode sandbox suite, which pins
///         the same file.
///     </para>
/// </summary>
internal sealed class InMemoryDaemonAttestationStore : IDockerDaemonAttestationStore
{
    private DockerDaemonAttestation? _attestation;

    public InMemoryDaemonAttestationStore(DockerDaemonAttestation? seed = null)
    {
        _attestation = seed;
    }

    /// <summary>How many times a pin was written, so a test can prove a read approved nothing.</summary>
    public int WriteCount { get; private set; }

    /// <summary>
    ///     Places a pin without counting it as a write, so a test that starts from an already-approved daemon can still
    ///     prove that the code under test approved nothing of its own.
    /// </summary>
    public void Seed(DockerDaemonAttestation attestation)
    {
        _attestation = attestation;
    }

    public Task<DockerDaemonAttestation?> ReadAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_attestation);
    }

    public Task WriteAsync(DockerDaemonAttestation attestation, CancellationToken cancellationToken = default)
    {
        _attestation = attestation;
        WriteCount++;
        return Task.CompletedTask;
    }
}

/// <summary>
///     The refusal the REAL <see cref="ContainerRuntimeResolver" /> writes for an endpoint that hides a value in its
///     query and its fragment.
///     <para>
///         It exists so that a suite asserting on a wire body — the 503 a log tail answers, the runtime card an
///         endpoint returns — asserts on the prose production produces rather than on a resolution the test wrote
///         itself. A hand-built resolution would pass whatever the redaction did, which is the one thing being
///         checked.
///     </para>
/// </summary>
internal static class DisclosingEndpointRefusal
{
    /// <summary>The value hidden in the query string.</summary>
    internal const string QuerySentinel = "sekrit-9f3a";

    /// <summary>The value hidden in the fragment.</summary>
    internal const string FragmentSentinel = "frag-sentinel-4d1c";

    private const string Raw = $"tcp://docker.remote:2375/?token={QuerySentinel}#{FragmentSentinel}";

    private static readonly DateTimeOffset Now = new(year: 2026, month: 9, day: 11, hour: 9, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Resolves that endpoint through the production resolver and returns the refusal it produced.</summary>
    internal static async Task<ContainerRuntimeResolution> ResolveAsync()
    {
        var endpoint = new DockerDaemonEndpoint { Uri = new Uri(Raw), Source = DockerDaemonEndpointSource.DockerHostEnvironmentVariable };
        var settingsStore = Substitute.For<INodeSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new StoredNodeSettings()));

        using var resolver = new ContainerRuntimeResolver(settingsStore,
            new StaticOptionsMonitor<ContainerRuntimeOptions>(new ContainerRuntimeOptions()),
            new UnusableContainerRuntimeFactory(),
            new InMemoryDaemonAttestationStore(),
            new FixedTimeProvider(Now),
            NullLogger<ContainerRuntimeResolver>.Instance,
            _ => endpoint);

        return await resolver.ResolveAsync();
    }

    /// <summary>A factory that cannot be used, so a refusal that reached it fails here rather than silently.</summary>
    private sealed class UnusableContainerRuntimeFactory : IContainerRuntimeFactory
    {
        public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint)
        {
            throw new InvalidOperationException("A client was constructed for an endpoint that had to be refused before one existed.");
        }

        public IContainerRuntime CreateRuntime(DockerDaemonEndpoint endpoint)
        {
            throw new InvalidOperationException("A runtime was constructed for an endpoint that had to be refused before one existed.");
        }
    }
}
