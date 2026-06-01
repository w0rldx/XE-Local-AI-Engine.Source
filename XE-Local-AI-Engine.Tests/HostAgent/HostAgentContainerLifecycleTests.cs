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
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            null,
            new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            });

        var containers = await service.ListContainersAsync(CancellationToken.None);

        AssertEx.ContainsSingle(containers, container =>
            container.Name == "ollama" && container.Health == ContainerHealth.Healthy && container.DigestVerified);
    }

    [Test]
    public async Task ListContainersAsync_ScopesToManifestOwnedComponentsAndPrunesTerminatedDuplicates()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                // Unowned cross-app daemon containers must never surface.
                CreateContainer("postgres-aspire-c0re-1", true, "bridge"),
                CreateContainer("pgadmin-c0re", true, "bridge"),
                // Owned component with a live instance plus a terminated stale duplicate of the same name.
                CreateContainer("ollama", false, "xe-engine-net"),
                CreateContainer("ollama", true, "xe-engine-net"),
                // Owned component that is currently running.
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

        var containers = await service.ListContainersAsync(CancellationToken.None);

        AssertEx.Equal(2, containers.Count);
        AssertEx.False(containers.Any(component => component.Name == "postgres-aspire-c0re-1"));
        AssertEx.False(containers.Any(component => component.Name == "pgadmin-c0re"));
        AssertEx.ContainsSingle(containers, component =>
            component.Name == "ollama" && component.Health == ContainerHealth.Healthy);
        AssertEx.ContainsSingle(containers, component =>
            component.Name == "xe-node-web-server" && component.Health == ContainerHealth.Healthy);
    }

    [Test]
    public async Task ListContainersAsync_WhenManifestMissing_ReturnsEmptyOwningNothing()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                CreateContainer("postgres-aspire-c0re-1", true, "bridge"),
                CreateContainer("pgadmin-c0re", true, "bridge"),
                CreateContainer("ollama", true, "xe-engine-net"),
                CreateContainer("xe-node-web-server", true, "xe-engine-net")
            ]
        };
        var service = new ContainerLifecycleService(docker, TimeProvider.System);

        var containers = await service.ListContainersAsync(CancellationToken.None);

        AssertEx.Equal(0, containers.Count);
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
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            null,
            new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            });

        var report = await service.StartContainerAsync("ollama", CancellationToken.None);

        AssertEx.Equal("start", report.Action);
        AssertEx.True(report.Succeeded);
        AssertEx.Equal(1, docker.StartCount);
    }

    [Test]
    public async Task StartContainerAsync_WhenContainerNotOwned_DeniesActionWithDiagnostic()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                CreateContainer("postgres-aspire-c0re-1", true, "bridge")
            ]
        };
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            null,
            new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            });

        var report = await service.StartContainerAsync("postgres-aspire-c0re-1", CancellationToken.None);

        AssertEx.Equal("start", report.Action);
        AssertEx.False(report.Succeeded);
        AssertEx.Contains(report.Diagnostics, "container-not-owned:postgres-aspire-c0re-1");
        AssertEx.Equal(0, docker.StartCount);
    }

    [Test]
    public async Task StartContainerAsync_WhenManifestMissing_DeniesActionAndDoesNotInvokeDocker()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                CreateContainer("ollama", false, "xe-engine-net")
            ]
        };
        var service = new ContainerLifecycleService(docker, TimeProvider.System);

        var report = await service.StartContainerAsync("ollama", CancellationToken.None);

        AssertEx.False(report.Succeeded);
        AssertEx.Contains(report.Diagnostics, "container-not-owned:ollama");
        AssertEx.Equal(0, docker.StartCount);
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
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            null,
            new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            });

        await service.StopContainerAsync("ollama", TimeSpan.FromSeconds(12), CancellationToken.None);

        AssertEx.Equal(1, docker.StopCount);
        AssertEx.Equal(TimeSpan.FromSeconds(12), docker.LastDrainTimeout);
    }

    [Test]
    public async Task StopContainerAsync_WhenContainerNotOwned_DeniesActionWithDiagnostic()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                CreateContainer("pgadmin-c0re", true, "bridge")
            ]
        };
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            null,
            new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            });

        var report = await service.StopContainerAsync("pgadmin-c0re", TimeSpan.FromSeconds(12), CancellationToken.None);

        AssertEx.Equal("stop", report.Action);
        AssertEx.False(report.Succeeded);
        AssertEx.Contains(report.Diagnostics, "container-not-owned:pgadmin-c0re");
        AssertEx.Equal(0, docker.StopCount);
    }

    [Test]
    public async Task RestartContainerAsync_WhenContainerNotOwned_DeniesActionWithDiagnostic()
    {
        var docker = new FakeDockerRuntimeClient
        {
            Containers =
            [
                CreateContainer("pgadmin-c0re", true, "bridge")
            ]
        };
        var service = new ContainerLifecycleService(docker,
            TimeProvider.System,
            null,
            new HostAgentRuntimeOptions
            {
                Manifest = CreateManifest()
            });

        var report = await service.RestartContainerAsync("pgadmin-c0re", TimeSpan.FromSeconds(12), CancellationToken.None);

        AssertEx.Equal("restart", report.Action);
        AssertEx.False(report.Succeeded);
        AssertEx.Contains(report.Diagnostics, "container-not-owned:pgadmin-c0re");
        AssertEx.Equal(0, docker.RestartCount);
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

        public int RestartCount { get; private set; }

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
            RestartCount++;
            LastDrainTimeout = drainTimeout;
            UpdateContainerRunningState(containerName, true);
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

        // Sandbox operations (Marker J-local) are not exercised by these container-lifecycle tests.
        public Task<string> CreateSandboxContainerAsync(SandboxContainerSpec spec, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> FindSandboxContainerAsync(string containerName, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<string, string>?> GetSandboxContainerLabelsAsync(string containerId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<DockerExecResult> ExecInContainerAsync(string containerId, DockerExecRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task CopyIntoContainerAsync(string containerId, string destinationPath, ReadOnlyMemory<byte> content, int fileMode, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<byte[]> ReadFromContainerAsync(string containerId, string sourcePath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task RemoveSandboxContainerAsync(string containerId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
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
