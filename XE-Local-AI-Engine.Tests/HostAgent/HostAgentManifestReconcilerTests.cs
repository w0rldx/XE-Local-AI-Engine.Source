namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Reconciliation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentManifestReconcilerTests
{
    private const string ExpectedDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    public void Parse_WhenReferenceIsCanonical_ReturnsRepositoryTagAndDigest()
    {
        var image = DockerImageReference.Parse($"ollama/ollama:0.11.10@sha256:{ExpectedDigest}");

        AssertEx.Equal("ollama/ollama", image.Repository);
        AssertEx.Equal("0.11.10", image.Tag);
        AssertEx.Equal(ExpectedDigest, image.Digest);
        AssertEx.Equal($"ollama/ollama:0.11.10@sha256:{ExpectedDigest}", image.CanonicalReference);
    }

    [Test]
    public async Task ReconcileAsync_WhenDigestMatches_PullsImageAndMarksComponentHealthy()
    {
        var docker = new FakeDockerRuntimeClient(ExpectedDigest);
        var reconciler = new ManifestReconciler(docker, TimeProvider.System);

        var result = await reconciler.ReconcileAsync(CreateManifest(ExpectedDigest));

        AssertEx.True(result.Succeeded);
        AssertEx.Empty(result.Diagnostics);
        AssertEx.ContainsSingle(docker.PulledImages, image => image.RepositoryWithTag == "ollama/ollama:0.11.10");
        AssertEx.ContainsSingle(result.Components, component =>
            component.Name == "ollama" && component.Health == ContainerHealth.Healthy && component.DigestVerified);
    }

    [Test]
    public async Task ReconcileAsync_WhenDigestMismatches_ReturnsImageDigestMismatch()
    {
        var docker = new FakeDockerRuntimeClient(OtherDigest);
        var reconciler = new ManifestReconciler(docker, TimeProvider.System);

        var result = await reconciler.ReconcileAsync(CreateManifest(ExpectedDigest));

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Diagnostics, $"{ReconcileDiagnosticCodes.ImageDigestMismatch}:ollama");
        AssertEx.ContainsSingle(result.Components, component =>
            component.Name == "ollama" && component.Health == ContainerHealth.Unhealthy && !component.DigestVerified);
    }

    [Test]
    public async Task ReconcileAsync_WhenPullModeDiffers_DoesNotCoalesceRequests()
    {
        var docker = new FakeDockerRuntimeClient(null)
        {
            ObservedDigestAfterPull = ExpectedDigest,
            DelayInspect = true
        };
        var reconciler = new ManifestReconciler(docker, TimeProvider.System);
        var manifest = CreateManifest(ExpectedDigest);

        var noPullTask = reconciler.ReconcileAsync(manifest, false);
        await docker.InspectStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var pullTask = reconciler.ReconcileAsync(manifest);
        docker.ReleaseInspect.TrySetResult();

        var noPullResult = await noPullTask;
        var pullResult = await pullTask;

        AssertEx.False(noPullResult.Succeeded);
        AssertEx.True(pullResult.Succeeded);
        AssertEx.Equal(1, docker.PulledImages.Count);
    }

    private static HostAgentManifest CreateManifest(string digest)
    {
        return new HostAgentManifest
        {
            SchemaVersion = 1,
            RuntimeMode = "managed",
            Models = new ModelManifest
            {
                BootstrapModel = "qwen3:0.6b",
                DefaultChatModel = "qwen3:8b"
            },
            Containers =
            [
                new ContainerManifest
                {
                    Name = "ollama",
                    Image = $"ollama/ollama:0.11.10@sha256:{digest}",
                    Network = "xe-engine-net",
                    Environment = new Dictionary<string, string>(),
                    Volumes = []
                }
            ],
            RuntimeLimits = new RuntimeLimitsManifest
            {
                MaxRuntimeDiskGb = 128,
                StopDrainTimeoutSeconds = 30
            }
        };
    }

    private sealed class FakeDockerRuntimeClient : IDockerRuntimeClient
    {
        private string? _observedDigest;

        public FakeDockerRuntimeClient(string? observedDigest)
        {
            _observedDigest = observedDigest;
        }

        public List<DockerImageReference> PulledImages { get; } = [];

        public string? ObservedDigestAfterPull { get; init; }

        public bool DelayInspect { get; init; }

        public TaskCompletionSource InspectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseInspect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task EnsureContainerAsync(ContainerManifest container, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task PullImageAsync(DockerImageReference image, CancellationToken cancellationToken)
        {
            PulledImages.Add(image);
            _observedDigest = ObservedDigestAfterPull ?? _observedDigest;
            return Task.CompletedTask;
        }

        public async Task<string?> InspectImageDigestAsync(DockerImageReference image, CancellationToken cancellationToken)
        {
            var observedDigest = _observedDigest;

            if (DelayInspect)
            {
                InspectStarted.TrySetResult();
                await ReleaseInspect.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return observedDigest;
        }

        public Task<IReadOnlyList<DockerContainerStatus>> ListContainersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DockerContainerStatus>>([]);
        }

        public Task StartContainerAsync(string containerName, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RestartContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<DockerLogLine> StreamLogsAsync(string containerName,
            int tailLines,
            bool follow,
            [EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
