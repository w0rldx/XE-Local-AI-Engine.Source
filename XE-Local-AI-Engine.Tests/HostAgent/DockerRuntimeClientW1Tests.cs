namespace XE_Local_AI_Engine.Tests.HostAgent;

using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;
using XE_Local_AI_Engine.HostAgent.Linux.Reconciliation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     W1 (§7.6) — DockerRuntimeClient config-Id fallback + reconciler acceptance tests.
///     These tests cover the four cases specified in Plans/2026-06-10-windows-installer-cli-plan.md §12.
/// </summary>
public sealed class DockerRuntimeClientW1Tests
{
    private const string Repository = "ghcr.io/c0re/xe-local-ai-engine";
    private const string Tag = "0.1.0-rc.1";

    // 64-char hex values that represent realistic config Id / RepoDigest hashes.
    private const string ConfigIdHex = "97e993db89d9a94b2d305c327003b86ae115b005d3e857c4f4fb8d160d15cf26";
    private const string WrongHex = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    // -------------------------------------------------------------------------
    // Test 1 — pulled image: existing RepoDigest path is byte-for-byte unchanged
    // -------------------------------------------------------------------------

    [Test]
    public async Task InspectImageDigest_WhenRepoDigestPresent_ReturnsRepoDigest_Unchanged()
    {
        // Arrange: image has a registry RepoDigest entry (pulled from a registry).
        var image = DockerImageReference.Parse($"{Repository}:{Tag}@sha256:{ConfigIdHex}");
        var repoDigestEntry = $"{Repository}@sha256:{ConfigIdHex}";

        var inspectResponse = new ImageInspectResponse
        {
            ID = $"sha256:{ConfigIdHex}",
            RepoDigests = [repoDigestEntry]
        };

        var imageOps = Substitute.For<IImageOperations>();
        imageOps.InspectImageAsync(image.RepositoryWithTag, Arg.Any<CancellationToken>())
            .Returns(inspectResponse);

        var dockerClient = Substitute.For<IDockerClient>();
        dockerClient.Images.Returns(imageOps);

        using var client = new DockerRuntimeClient(dockerClient);

        // Act
        var result = await client.InspectImageDigestAsync(image, CancellationToken.None);

        // Assert: returns the bare hex from the RepoDigest entry, unchanged from pre-W1 behaviour.
        AssertEx.Equal(ConfigIdHex, result);
    }

    // -------------------------------------------------------------------------
    // Test 2 — loaded image: no matching RepoDigest → falls back to config Id
    // -------------------------------------------------------------------------

    [Test]
    public async Task InspectImageDigest_WhenRepoDigestsEmpty_FallsBackToConfigId()
    {
        // Arrange: image was loaded via docker load; RepoDigests is empty (no registry push/pull).
        var image = DockerImageReference.Parse($"{Repository}:{Tag}@sha256:{ConfigIdHex}");

        var inspectResponse = new ImageInspectResponse
        {
            ID = $"sha256:{ConfigIdHex}",
            RepoDigests = [] // empty — typical for a docker save/load cycle
        };

        var imageOps = Substitute.For<IImageOperations>();
        imageOps.InspectImageAsync(image.RepositoryWithTag, Arg.Any<CancellationToken>())
            .Returns(inspectResponse);

        var dockerClient = Substitute.For<IDockerClient>();
        dockerClient.Images.Returns(imageOps);

        using var client = new DockerRuntimeClient(dockerClient);

        // Act
        var result = await client.InspectImageDigestAsync(image, CancellationToken.None);

        // Assert: falls back to the config Id hex (sha256:<hex> → stripped to bare hex).
        AssertEx.Equal(ConfigIdHex, result);
    }

    // -------------------------------------------------------------------------
    // Test 3 — reconciler: manifest carrying config Id → Healthy + DigestVerified
    // -------------------------------------------------------------------------

    [Test]
    public async Task Reconcile_WhenLoadedImageIdMatchesManifest_Healthy()
    {
        // Arrange: manifest carries the config Id (W1 semantics for managed loaded images).
        // FakeDockerRuntimeClient.InspectImageDigestAsync returns image.Digest directly,
        // which simulates W1's behaviour (the method now returns the config Id for loaded images).
        var docker = new LoadedImageFakeDockerRuntimeClient(ConfigIdHex);
        var reconciler = new ManifestReconciler(docker, TimeProvider.System);
        var manifest = CreateManifest(ConfigIdHex);

        // Act
        var result = await reconciler.ReconcileAsync(manifest, pullImages: false);

        // Assert: reconciler accepts the config Id and marks the component healthy.
        AssertEx.True(result.Succeeded);
        AssertEx.Empty(result.Diagnostics);
        AssertEx.ContainsSingle(result.Components, component =>
            component.Name == "xe-node-web-server" &&
            component.Health == ContainerHealth.Healthy &&
            component.DigestVerified);
    }

    // -------------------------------------------------------------------------
    // Test 4 — reconciler: wrong config Id still fail-closes (guarantee not weakened)
    // -------------------------------------------------------------------------

    [Test]
    public async Task Reconcile_WhenLoadedImageIdMismatch_StillFailsClosed()
    {
        // Arrange: fake returns a DIFFERENT hex than what the manifest carries.
        var docker = new LoadedImageFakeDockerRuntimeClient(WrongHex);
        var reconciler = new ManifestReconciler(docker, TimeProvider.System);
        var manifest = CreateManifest(ConfigIdHex);

        // Act
        var result = await reconciler.ReconcileAsync(manifest, pullImages: false);

        // Assert: still fail-closes; W1 did not weaken the mismatch guarantee.
        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Diagnostics, $"{ReconcileDiagnosticCodes.ImageDigestMismatch}:xe-node-web-server");
        AssertEx.ContainsSingle(result.Components, component =>
            component.Name == "xe-node-web-server" &&
            component.Health == ContainerHealth.Unhealthy &&
            !component.DigestVerified);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HostAgentManifest CreateManifest(string digest)
    {
        return new HostAgentManifest
        {
            SchemaVersion = 1,
            RuntimeMode = "managed",
            Models = new ModelManifest
            {
                BootstrapModel = "qwen3:0.6b",
                DefaultChatModel = "qwen3:0.6b"
            },
            Containers =
            [
                new ContainerManifest
                {
                    Name = "xe-node-web-server",
                    Image = $"{Repository}:{Tag}@sha256:{digest}",
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

    /// <summary>
    ///     Minimal <see cref="IDockerRuntimeClient" /> that returns a controlled digest from
    ///     <see cref="InspectImageDigestAsync" />, simulating the W1 fallback returning a config Id
    ///     rather than a registry RepoDigest.  Only the methods exercised by <see cref="ManifestReconciler" />
    ///     are implemented; all others throw <see cref="NotSupportedException" />.
    /// </summary>
    private sealed class LoadedImageFakeDockerRuntimeClient(string? observedDigest) : IDockerRuntimeClient
    {
        public Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EnsureContainerAsync(ContainerManifest container, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PullImageAsync(DockerImageReference image, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> InspectImageDigestAsync(DockerImageReference image, CancellationToken cancellationToken) =>
            Task.FromResult(observedDigest);

        public Task<IReadOnlyList<DockerContainerStatus>> ListContainersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DockerContainerStatus>>([]);

        public Task StartContainerAsync(string containerName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestartContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public IAsyncEnumerable<DockerLogLine> StreamLogsAsync(string containerName, int tailLines, bool follow, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> CreateSandboxContainerAsync(SandboxContainerSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> FindSandboxContainerAsync(string containerName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>?> GetSandboxContainerLabelsAsync(string containerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DockerExecResult> ExecInContainerAsync(string containerId, DockerExecRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CopyIntoContainerAsync(string containerId, string destinationPath, ReadOnlyMemory<byte> content, int fileMode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> ReadFromContainerAsync(string containerId, string sourcePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RemoveSandboxContainerAsync(string containerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UtilityContainerRunResult> RunUtilityContainerAsync(UtilityContainerRunSpec spec, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RemoveOrphanedUtilityContainersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
