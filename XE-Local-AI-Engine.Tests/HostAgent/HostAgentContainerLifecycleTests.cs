namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;
using XE_Local_AI_Engine.HostAgent.Linux.Reconciliation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentContainerLifecycleTests
{
    private const string ExpectedDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Test]
    public async Task ListContainersAsync_MapsDockerStateToRuntimeStatus()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                new DockerContainerStatus
                {
                    Name = "ollama",
                    ImageReference = "ollama/ollama:0.11.10@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    State = "running",
                    IsRunning = true
                }
            ]
        };
        var service = new ContainerLifecycleService(docker, TimeProvider.System);

        var containers = await service.ListContainersAsync(CancellationToken.None);

        AssertEx.ContainsSingle(containers, container =>
            container.Name == "ollama" && container.Health == ContainerHealth.Healthy && container.DigestVerified);
    }

    [Test]
    public async Task StartContainerAsync_WhenContainerIsStopped_DelegatesStartAndReturnsReport()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                new DockerContainerStatus
                {
                    Name = "ollama",
                    ImageReference = "ollama/ollama:0.11.10@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    State = "exited",
                    IsRunning = false
                }
            ]
        };
        var service = new ContainerLifecycleService(docker, TimeProvider.System);

        var report = await service.StartContainerAsync("ollama", CancellationToken.None);

        AssertEx.Equal("start", report.Action);
        AssertEx.True(report.Succeeded);
        AssertEx.Equal(1, docker.StartCount);
    }

    [Test]
    public async Task StopContainerAsync_WhenContainerIsRunning_DelegatesStopWithDrainTimeout()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                new DockerContainerStatus
                {
                    Name = "ollama",
                    ImageReference = "ollama/ollama:0.11.10@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    State = "running",
                    IsRunning = true
                }
            ]
        };
        var service = new ContainerLifecycleService(docker, TimeProvider.System);

        await service.StopContainerAsync("ollama", TimeSpan.FromSeconds(12), CancellationToken.None);

        AssertEx.Equal(1, docker.StopCount);
        AssertEx.Equal(TimeSpan.FromSeconds(12), docker.LastDrainTimeout);
    }

    [Test]
    public async Task StopAllContainersAsync_StopsOnlyRuntimeNetworkContainersAndReturnsReport()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                CreateContainer("ollama", true, "xe-engine-net"),
                CreateContainer("xe-node-web-server", true, "xe-engine-net"),
                CreateContainer("unrelated", true, "other-net")
            ]
        };
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            null,
            new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            });

        var report = await service.StopAllContainersAsync(TimeSpan.FromSeconds(12), CancellationToken.None);

        AssertEx.Equal("stop-all", report.Action);
        AssertEx.True(report.Succeeded);
        AssertEx.Equal(2, docker.StoppedContainers.Count);
        AssertEx.Contains(docker.StoppedContainers, "ollama");
        AssertEx.Contains(docker.StoppedContainers, "xe-node-web-server");
        AssertEx.False(docker.StoppedContainers.Contains("unrelated", StringComparer.Ordinal));
        AssertEx.Equal(TimeSpan.FromSeconds(12), docker.LastDrainTimeout);
    }

    [Test]
    public async Task StopAllContainersAsync_IssuesStopRequestsConcurrently()
    {
        var docker = new FakeDockerRuntimeClient
        {
            ExpectedConcurrentStopCount = 2,
            Containers =
            [
                CreateContainer("ollama", true, "xe-engine-net"),
                CreateContainer("xe-node-web-server", true, "xe-engine-net")
            ]
        };
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            null,
            new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            });

        var stopTask = service.StopAllContainersAsync(TimeSpan.FromSeconds(12), CancellationToken.None);

        try
        {
            await docker.AllExpectedStopRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            docker.ReleaseStopRequests.TrySetResult();
        }

        var report = await stopTask;

        AssertEx.True(report.Succeeded);
        AssertEx.Equal(2, docker.StoppedContainers.Count);
    }

    [Test]
    public async Task StartAllContainersAsync_OnCleanInstall_ReconcilesStaticManifestAndCreatesRuntimeContainers()
    {
        var docker = new FakeDockerRuntimeClient
        {
            ObservedDigest = ExpectedDigest,
            Containers =
            [
                CreateContainer("ollama", false, "xe-engine-net"),
                CreateContainer("xe-node-web-server", false, "xe-engine-net")
            ]
        };
        var manifest = CreateManifest();
        var reconciler = new ManifestReconciler(docker, TimeProvider.System);
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            reconciler,
            new HostAgentRuntimeOptions
            {
                Manifest = manifest
            });

        var report = await service.StartAllContainersAsync(CancellationToken.None);

        AssertEx.Equal("start-all", report.Action);
        AssertEx.True(report.Succeeded);
        AssertEx.Equal(2, docker.PullCount);
        AssertEx.ContainsSingle(docker.EnsuredNetworks, network => network == "xe-engine-net");
        AssertEx.Equal(2, docker.EnsuredContainers.Count);
        AssertEx.Equal(2, docker.StartedContainers.Count);
        AssertEx.Contains(docker.StartedContainers, "ollama");
        AssertEx.Contains(docker.StartedContainers, "xe-node-web-server");
    }

    [Test]
    public async Task StartAllContainersAsync_WhenCachedDigestMismatches_DoesNotStartContainers()
    {
        var docker = new FakeDockerRuntimeClient
        {
            ObservedDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Containers =
            [
                CreateContainer("ollama", false, "xe-engine-net"),
                CreateContainer("xe-node-web-server", false, "xe-engine-net")
            ]
        };
        var manifest = CreateManifest();
        var reconciler = new ManifestReconciler(docker, TimeProvider.System);
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            reconciler,
            new HostAgentRuntimeOptions
            {
                Manifest = manifest
            });

        var report = await service.StartAllContainersAsync(CancellationToken.None);

        AssertEx.False(report.Succeeded);
        AssertEx.Equal(0, docker.StartCount);
        AssertEx.Contains(report.Diagnostics, $"{ReconcileDiagnosticCodes.ImageDigestMismatch}:ollama");
    }

    private static DockerContainerStatus CreateContainer(string name, bool isRunning, params string[] networks)
    {
        return new DockerContainerStatus
        {
            Name = name,
            ImageReference = $"{name}:0.1.0@sha256:{ExpectedDigest}",
            NetworkNames = networks,
            State = isRunning ? "running" : "exited",
            IsRunning = isRunning
        };
    }

    private static HostAgentManifest CreateManifest()
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
                    Image = $"ollama/ollama:0.11.10@sha256:{ExpectedDigest}",
                    Network = "xe-engine-net",
                    Environment = new Dictionary<string, string>(),
                    Volumes = []
                },
                new ContainerManifest
                {
                    Name = "xe-node-web-server",
                    Image = $"ghcr.io/c0re/xe-local-ai-engine:0.1.0@sha256:{ExpectedDigest}",
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
        public List<DockerContainerStatus> Containers { get; init; } = [];

        public string? ObservedDigest { get; init; }

        public int PullCount { get; private set; }

        public int ExpectedConcurrentStopCount { get; init; }

        public int StartCount { get; private set; }

        public List<string> EnsuredNetworks { get; } = [];

        public List<string> EnsuredContainers { get; } = [];

        public int StopCount { get; private set; }

        public TimeSpan LastDrainTimeout { get; private set; }

        public List<string> StartedContainers { get; } = [];

        public List<string> StoppedContainers { get; } = [];

        public TaskCompletionSource AllExpectedStopRequestsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStopRequests { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
        {
            EnsuredNetworks.Add(networkName);
            return Task.CompletedTask;
        }

        public Task EnsureContainerAsync(ContainerManifest container, CancellationToken cancellationToken)
        {
            EnsuredContainers.Add(container.Name);
            if (Containers.All(existing => !string.Equals(existing.Name, container.Name, StringComparison.Ordinal)))
            {
                Containers.Add(new DockerContainerStatus
                {
                    Name = container.Name,
                    ImageReference = container.Image,
                    NetworkNames = [container.Network],
                    State = "created",
                    IsRunning = false
                });
            }

            return Task.CompletedTask;
        }

        public Task PullImageAsync(DockerImageReference image, CancellationToken cancellationToken)
        {
            PullCount++;
            return Task.CompletedTask;
        }

        public Task<string?> InspectImageDigestAsync(DockerImageReference image, CancellationToken cancellationToken)
        {
            return Task.FromResult(ObservedDigest);
        }

        public Task<IReadOnlyList<DockerContainerStatus>> ListContainersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DockerContainerStatus>>(Containers);
        }

        public Task StartContainerAsync(string containerName, CancellationToken cancellationToken)
        {
            StartCount++;
            StartedContainers.Add(containerName);
            UpdateContainerRunningState(containerName, true);
            return Task.CompletedTask;
        }

        public async Task StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
        {
            StopCount++;
            StoppedContainers.Add(containerName);
            LastDrainTimeout = drainTimeout;

            if (ExpectedConcurrentStopCount > 0 && StoppedContainers.Count >= ExpectedConcurrentStopCount)
            {
                AllExpectedStopRequestsStarted.TrySetResult();
            }

            if (ExpectedConcurrentStopCount > 0)
            {
                await ReleaseStopRequests.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            UpdateContainerRunningState(containerName, false);
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

        private void UpdateContainerRunningState(string containerName, bool isRunning)
        {
            var index = Containers.FindIndex(container => string.Equals(container.Name, containerName, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            Containers[index] = Containers[index] with
            {
                State = isRunning ? "running" : "exited",
                IsRunning = isRunning
            };
        }
    }
}
