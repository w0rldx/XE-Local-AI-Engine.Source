namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;
using XE_Local_AI_Engine.HostAgent.Linux.Logs;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentContainerLogTests
{
    [Test]
    public async Task StreamLogsAsync_ForwardsTailFollowAndLines()
    {
        var docker = new FakeDockerRuntimeClient
        {
            LogLines =
            [
                new DockerLogLine
                {
                    ContainerName = "ollama",
                    Stream = "stdout",
                    Line = "ready",
                    ObservedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var service = new ContainerLogService(docker, new HostAgentRuntimeOptions
        {
            Manifest = CreateManifest()
        });
        var observed = new List<DockerLogLine>();

        await foreach (var line in service.StreamLogsAsync("ollama", 25, true, CancellationToken.None))
        {
            observed.Add(line);
        }

        AssertEx.Equal("ollama", docker.LastContainerName);
        AssertEx.Equal(25, docker.LastTailLines);
        AssertEx.True(docker.LastFollow);
        AssertEx.ContainsSingle(observed, line => line.Line == "ready");
    }

    [Test]
    public async Task StreamLogsAsync_WhenContainerNotOwned_YieldsNothingAndDoesNotInvokeDocker()
    {
        var docker = new FakeDockerRuntimeClient
        {
            LogLines =
            [
                new DockerLogLine
                {
                    ContainerName = "postgres-aspire-c0re-1",
                    Stream = "stdout",
                    Line = "ready",
                    ObservedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var service = new ContainerLogService(docker, new HostAgentRuntimeOptions
        {
            Manifest = CreateManifest()
        });
        var observed = new List<DockerLogLine>();

        await foreach (var line in service.StreamLogsAsync("postgres-aspire-c0re-1", 25, true, CancellationToken.None))
        {
            observed.Add(line);
        }

        AssertEx.Equal(0, observed.Count);
        AssertEx.Equal(string.Empty, docker.LastContainerName);
    }

    [Test]
    public async Task StreamLogsAsync_WhenManifestMissing_YieldsNothingAndDoesNotInvokeDocker()
    {
        var docker = new FakeDockerRuntimeClient
        {
            LogLines =
            [
                new DockerLogLine
                {
                    ContainerName = "ollama",
                    Stream = "stdout",
                    Line = "ready",
                    ObservedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var service = new ContainerLogService(docker);
        var observed = new List<DockerLogLine>();

        await foreach (var line in service.StreamLogsAsync("ollama", 25, true, CancellationToken.None))
        {
            observed.Add(line);
        }

        AssertEx.Equal(0, observed.Count);
        AssertEx.Equal(string.Empty, docker.LastContainerName);
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
                    Image = "ollama/ollama:0.11.10@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
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
        public IReadOnlyList<DockerLogLine> LogLines { get; init; } = [];

        public string LastContainerName { get; private set; } = string.Empty;

        public int LastTailLines { get; private set; }

        public bool LastFollow { get; private set; }

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
            return Task.CompletedTask;
        }

        public Task<string?> InspectImageDigestAsync(DockerImageReference image, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
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
            LastContainerName = containerName;
            LastTailLines = tailLines;
            LastFollow = follow;

            foreach (var line in LogLines)
            {
                yield return line;
            }

            await Task.CompletedTask;
        }

        // Sandbox operations (Marker J-local) are not exercised by these container-log tests.
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
    }
}
