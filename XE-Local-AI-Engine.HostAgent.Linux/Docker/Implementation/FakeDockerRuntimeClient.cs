namespace XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

/// <summary>
///     Client boundary for fake docker runtime operations.
/// </summary>
public sealed class FakeDockerRuntimeClient : IDockerRuntimeClient
{
    private const string RuntimeNetwork = "xe-engine-net";
    private readonly ConcurrentDictionary<string, DockerContainerStatus> _containers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FakeSandbox> _sandboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScriptedExec> _scriptedExecs = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    // --- Model-fit utility one-shot run (scriptable) ---

    private readonly object _utilitySync = new();
    private int _sandboxCounter;
    private ScriptedUtilityRun _scriptedUtilityRun = new(0, string.Empty, string.Empty, false);

    public FakeDockerRuntimeClient(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        SeedContainer("ollama", "ollama/ollama:dev@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        SeedContainer("xe-node-web-server", "ghcr.io/c0re/xe-local-ai-engine:dev@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    }

    /// <summary>Test seam: the spec passed to the last <see cref="RunUtilityContainerAsync" /> call, or <c>null</c> if never called.</summary>
    public UtilityContainerRunSpec? LastUtilityRunSpec { get; private set; }

    /// <summary>Test seam: whether the utility container created by the last run was removed (cleanup assertion).</summary>
    public bool LastUtilityContainerRemoved { get; private set; }

    /// <summary>Test seam: how many times <see cref="RunUtilityContainerAsync" /> was invoked.</summary>
    public int UtilityRunCount { get; private set; }

    /// <summary>Test seam: how many orphaned utility containers <see cref="RemoveOrphanedUtilityContainersAsync" /> reports.</summary>
    public int OrphanedUtilityContainerCount { get; set; }

    public Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task EnsureContainerAsync(ContainerManifest container, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        cancellationToken.ThrowIfCancellationRequested();

        _containers.TryAdd(container.Name, CreateContainer(container.Name, container.Image, false));
        return Task.CompletedTask;
    }

    public Task PullImageAsync(DockerImageReference image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<string?> InspectImageDigestAsync(DockerImageReference image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<string?>(image.Digest);
    }

    public Task<IReadOnlyList<DockerContainerStatus>> ListContainersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DockerContainerStatus>>(_containers.Values.OrderBy(static container => container.Name, StringComparer.Ordinal).ToArray());
    }

    public Task StartContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        UpdateContainer(containerName, true, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        UpdateContainer(containerName, false, cancellationToken);
        return Task.CompletedTask;
    }

    public Task RestartContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        UpdateContainer(containerName, true, cancellationToken);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<DockerLogLine> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();

        var count = Math.Max(1, tailLines == 0 ? 1 : tailLines);
        for (var index = 0; index < count; index++)
        {
            yield return CreateLogLine(containerName, $"fake docker log {index + 1}");
        }

        while (follow && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            yield return CreateLogLine(containerName, "fake docker heartbeat");
        }
    }

    public Task<string> CreateSandboxContainerAsync(SandboxContainerSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Name);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = _sandboxes.Values.FirstOrDefault(sandbox => string.Equals(sandbox.Name, spec.Name, StringComparison.Ordinal));
        if (existing is not null)
        {
            return Task.FromResult(existing.ContainerId);
        }

        var id = $"fake-sandbox-{Interlocked.Increment(ref _sandboxCounter).ToString(CultureInfo.InvariantCulture)}";
        _sandboxes[id] = new FakeSandbox(id, spec.Name, spec);
        return Task.FromResult(id);
    }

    public Task<string?> FindSandboxContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();

        var match = _sandboxes.Values.FirstOrDefault(sandbox => string.Equals(sandbox.Name, containerName, StringComparison.Ordinal));
        return Task.FromResult(match?.ContainerId);
    }

    public Task<IReadOnlyDictionary<string, string>?> GetSandboxContainerLabelsAsync(string containerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_sandboxes.TryGetValue(containerId, out var sandbox))
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        }

        var labels = new Dictionary<string, string>(sandbox.Spec.Labels, StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, string>?>(labels);
    }

    public async Task<DockerExecResult> ExecInContainerAsync(string containerId, DockerExecRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(request);

        // Validate the sandbox is alive before running (mirrors the real client's container-not-found behavior).
        _ = GetAliveSandbox(containerId);
        var commandLine = string.Join(' ', new[]
        {
            request.Executable
        }.Concat(request.Arguments));
        var scripted = _scriptedExecs.TryGetValue(commandLine, out var match)
            ? match
            : new ScriptedExec(0, string.Empty, string.Empty, false);

        if (scripted.Blocks)
        {
            using var timeoutCts = request.Timeout is { } timeout && timeout > TimeSpan.Zero
                ? new CancellationTokenSource(timeout)
                : new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new DockerExecResult
                {
                    ExecutionId = request.ExecutionId,
                    ExitCode = -1,
                    Completed = false
                };
            }
        }

        return new DockerExecResult
        {
            ExecutionId = request.ExecutionId,
            ExitCode = scripted.ExitCode,
            StandardOutput = scripted.StandardOutput,
            StandardError = scripted.StandardError,
            Completed = true
        };
    }

    public Task CopyIntoContainerAsync(string containerId,
        string destinationPath,
        ReadOnlyMemory<byte> content,
        int fileMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var sandbox = GetAliveSandbox(containerId);
        sandbox.Files[destinationPath] = content.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadFromContainerAsync(string containerId, string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        var sandbox = GetAliveSandbox(containerId);
        if (!sandbox.Files.TryGetValue(sourcePath, out var content))
        {
            throw new FileNotFoundException($"Sandbox path '{sourcePath}' was not found.", sourcePath);
        }

        return Task.FromResult(content);
    }

    public Task RemoveSandboxContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        cancellationToken.ThrowIfCancellationRequested();

        _sandboxes.TryRemove(containerId, out _);
        return Task.CompletedTask;
    }

    public async Task<UtilityContainerRunResult> RunUtilityContainerAsync(UtilityContainerRunSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Image);

        ScriptedUtilityRun scripted;
        lock (_utilitySync)
        {
            LastUtilityRunSpec = spec;
            LastUtilityContainerRemoved = false;
            UtilityRunCount++;
            scripted = _scriptedUtilityRun;
        }

        if (scripted.Blocks)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation/timeout cleanup: the container is stopped/removed unless debug retention keeps it.
                lock (_utilitySync)
                {
                    LastUtilityContainerRemoved = !spec.RetainOnFailure;
                }

                return new UtilityContainerRunResult
                {
                    ExitCode = -1,
                    Completed = false
                };
            }
        }

        lock (_utilitySync)
        {
            // The normal path removes the container unless it failed AND debug retention was requested.
            LastUtilityContainerRemoved = !(scripted.ExitCode != 0 && spec.RetainOnFailure);
        }

        return new UtilityContainerRunResult
        {
            ExitCode = scripted.ExitCode,
            StandardOutput = scripted.StandardOutput,
            StandardError = scripted.StandardError,
            Completed = true
        };
    }

    public Task<int> RemoveOrphanedUtilityContainersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OrphanedUtilityContainerCount);
    }

    /// <summary>Test seam: the spec recorded for a created sandbox container (asserts limits/network/labels mapping).</summary>
    public SandboxContainerSpec? TryGetRecordedSpec(string containerId)
    {
        return _sandboxes.TryGetValue(containerId, out var sandbox) ? sandbox.Spec : null;
    }

    /// <summary>Test seam: scripts an exec result for the given executable+args join (default behavior is an empty echo).</summary>
    public void ScriptExec(string commandLine, int exitCode, string standardOutput = "", string standardError = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        _scriptedExecs[commandLine] = new ScriptedExec(exitCode, standardOutput, standardError, false);
    }

    /// <summary>Test seam: makes an exec block until its cancellation token fires, so cancel/timeout paths are testable.</summary>
    public void ScriptBlockingExec(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        _scriptedExecs[commandLine] = new ScriptedExec(0, string.Empty, string.Empty, true);
    }

    /// <summary>Test seam: scripts the result the next utility run returns (default behavior is exit 0, empty output).</summary>
    public void ScriptUtilityRun(int exitCode, string standardOutput = "", string standardError = "")
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        lock (_utilitySync)
        {
            _scriptedUtilityRun = new ScriptedUtilityRun(exitCode, standardOutput, standardError, false);
        }
    }

    /// <summary>Test seam: makes the next utility run block until its cancellation token fires, so cancel/timeout is testable.</summary>
    public void ScriptBlockingUtilityRun()
    {
        lock (_utilitySync)
        {
            _scriptedUtilityRun = new ScriptedUtilityRun(0, string.Empty, string.Empty, true);
        }
    }

    private FakeSandbox GetAliveSandbox(string containerId)
    {
        return _sandboxes.TryGetValue(containerId, out var sandbox)
            ? sandbox
            : throw new InvalidOperationException($"Sandbox container '{containerId}' was not found (it may have been removed).");
    }

    private void SeedContainer(string name, string imageReference)
    {
        _containers[name] = CreateContainer(name, imageReference, true);
    }

    private void UpdateContainer(string containerName, bool isRunning, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();

        _containers.AddOrUpdate(containerName,
            name => CreateContainer(name, $"local/{name}:dev@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", isRunning),
            (_, existing) => existing with
            {
                State = isRunning ? "running" : "exited",
                IsRunning = isRunning
            });
    }

    private static DockerContainerStatus CreateContainer(string name, string imageReference, bool isRunning)
    {
        return new DockerContainerStatus
        {
            Name = name,
            ImageReference = imageReference,
            State = isRunning ? "running" : "exited",
            IsRunning = isRunning,
            NetworkNames = [RuntimeNetwork]
        };
    }

    private DockerLogLine CreateLogLine(string containerName, string line)
    {
        return new DockerLogLine
        {
            ContainerName = containerName,
            Stream = "stdout",
            Line = line,
            ObservedAt = _timeProvider.GetUtcNow()
        };
    }

    private sealed class FakeSandbox
    {
        public FakeSandbox(string containerId, string name, SandboxContainerSpec spec)
        {
            ContainerId = containerId;
            Name = name;
            Spec = spec;
        }

        public string ContainerId { get; }

        public string Name { get; }

        public SandboxContainerSpec Spec { get; }

        public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ScriptedExec(int ExitCode, string StandardOutput, string StandardError, bool Blocks);

    private sealed record ScriptedUtilityRun(int ExitCode, string StandardOutput, string StandardError, bool Blocks);
}
