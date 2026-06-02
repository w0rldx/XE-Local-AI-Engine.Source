namespace XE_Local_AI_Engine.Client.Services.Sandbox.Fake;

using System.Globalization;

/// <summary>
///     Deterministic, in-memory <see cref="ISandboxRuntimeProvider" /> used as the CI-mandatory provider and as the
///     default until a real provider ships. It needs no Docker and no network:
///     a virtual filesystem backs copy/read, command results are scripted, and a "blocking" command lets
///     cancellation and kill be exercised honestly. All timestamps come from the injected <see cref="TimeProvider" />
///     so behavior is reproducible. Mirrors the production-resident, config-selected <c>FakeDockerRuntimeClient</c>.
/// </summary>
public sealed class FakeSandboxRuntimeProvider : ISandboxRuntimeProvider
{
    /// <summary>The provider name this fake registers under for configuration-bound selection.</summary>
    public const string Name = "fake";

    private readonly List<SandboxCommandRequest> _executed = [];
    private readonly Dictionary<string, string> _hostFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SandboxState> _sandboxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptedCommand> _scripts = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private int _sandboxCounter;

    public FakeSandboxRuntimeProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string ProviderName => Name;

    public SandboxProviderCapabilities Capabilities =>
        SandboxProviderCapabilities.SupportsCopyInto
        | SandboxProviderCapabilities.SupportsCopyOut
        | SandboxProviderCapabilities.SupportsCommandCancellation
        | SandboxProviderCapabilities.SupportsAttach
        | SandboxProviderCapabilities.SupportsKill;

    // ---- test/seed seams (the fake is a production-resident, config-selected double) ----

    /// <summary>Seed a host file so a later <see cref="CopyIntoAsync" /> has content to copy.</summary>
    public void WriteHostFile(string hostPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentNullException.ThrowIfNull(content);

        lock (_sync)
        {
            _hostFiles[hostPath] = content;
        }
    }

    /// <summary>Read a host file previously written or copied out; returns <see langword="null" /> when absent.</summary>
    public string? TryReadHostFile(string hostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        lock (_sync)
        {
            return _hostFiles.TryGetValue(hostPath, out var content) ? content : null;
        }
    }

    /// <summary>Register a deterministic result for a command line (<c>executable</c> plus space-joined arguments).</summary>
    public void RegisterCommand(string commandLine, int exitCode, string standardOutput = "", string standardError = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        lock (_sync)
        {
            _scripts[commandLine] = new ScriptedCommand(false, exitCode, standardOutput, standardError);
        }
    }

    /// <summary>Register a command that blocks until it is cancelled or the sandbox is killed (for cancel/kill tests).</summary>
    public void RegisterBlockingCommand(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        lock (_sync)
        {
            _scripts[commandLine] = new ScriptedCommand(true, 0, string.Empty, string.Empty);
        }
    }

    /// <summary>
    ///     The in-sandbox destination paths that currently hold copied content, sorted ordinally. Lets a workspace-copy
    ///     test (workspace copy) assert exactly which files survived the exclusion rules.
    /// </summary>
    public IReadOnlyList<string> SnapshotSandboxPaths(SandboxHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        lock (_sync)
        {
            var state = GetAliveState(handle);
            return state.SandboxFiles.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    ///     Every command passed to <see cref="ExecuteAsync" />, in order. Lets a test assert that the workspace git
    ///     baseline and patch export issued the expected command sequence.
    /// </summary>
    public IReadOnlyList<SandboxCommandRequest> ExecutedCommands
    {
        get
        {
            lock (_sync)
            {
                return _executed.ToArray();
            }
        }
    }

    // ---- ISandboxRuntimeProvider ----

    public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var attached = FindAliveByKey(request.AttachKey);
            if (attached is not null)
            {
                return Task.FromResult(attached.Handle);
            }

            EvictOwnerConflicts(request.AttachKey);

            _sandboxCounter++;
            var sandboxId = "fake-sandbox-" + _sandboxCounter.ToString(CultureInfo.InvariantCulture);
            var handle = new SandboxHandle
            {
                ProviderName = Name,
                SandboxId = sandboxId,
                AttachKey = request.AttachKey,
                CreatedAt = _timeProvider.GetUtcNow(),
                ManifestVersion = request.AttachKey.ManifestVersion
            };
            _sandboxes[sandboxId] = new SandboxState(handle);
            return Task.FromResult(handle);
        }
    }

    public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var match = FindAliveByKey(attachKey)
                        ?? throw new SandboxHandleInvalidException("No live sandbox matches the supplied attach key.");
            return Task.FromResult(match.Handle);
        }
    }

    public async Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = _timeProvider.GetUtcNow();
        ScriptedCommand scripted;
        InFlightExecution? inFlight = null;
        lock (_sync)
        {
            var state = GetAliveState(handle);
            _executed.Add(request);
            scripted = ResolveScript(request);
            if (scripted.Blocks)
            {
                if (state.InFlight.ContainsKey(request.ExecutionId))
                {
                    throw new InvalidOperationException($"Execution id '{request.ExecutionId}' is already in flight for this sandbox.");
                }

                inFlight = new InFlightExecution();
                state.InFlight[request.ExecutionId] = inFlight;
            }
        }

        if (inFlight is null)
        {
            return BuildResult(request, scripted, true, startedAt);
        }

        using var registration = cancellationToken.Register(
            static state => ((InFlightExecution)state!).Completion.TrySetCanceled(),
            inFlight);

        bool completedNormally;
        try
        {
            completedNormally = await inFlight.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            RemoveInFlight(handle.SandboxId, request.ExecutionId, inFlight);
        }

        return BuildResult(request, scripted, completedNormally, startedAt);
    }

    public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var state = GetAliveState(handle);
            state.SandboxFiles[request.DestinationPath] = ResolveCopyContent(request.SourcePath);
        }

        return Task.CompletedTask;
    }

    private string ResolveCopyContent(string sourcePath)
    {
        // Prefer explicitly seeded content (unit tests). Otherwise fall back to the real file on disk so a workspace
        // copy test (workspace copy) can walk a real temp source tree without pre-seeding every surviving file.
        if (_hostFiles.TryGetValue(sourcePath, out var seeded))
        {
            return seeded;
        }

        if (File.Exists(sourcePath))
        {
            return File.ReadAllText(sourcePath);
        }

        throw new FileNotFoundException($"Host source path '{sourcePath}' has no seeded content and does not exist on disk.", sourcePath);
    }

    public Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var state = GetAliveState(handle);
            if (!state.SandboxFiles.TryGetValue(sandboxPath, out var content))
            {
                throw new FileNotFoundException($"Sandbox path '{sandboxPath}' was not found.", sandboxPath);
            }

            return Task.FromResult(content);
        }
    }

    public Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var state = GetAliveState(handle);
            if (!state.SandboxFiles.TryGetValue(request.SourcePath, out var content))
            {
                throw new FileNotFoundException($"Sandbox path '{request.SourcePath}' was not found.", request.SourcePath);
            }

            _hostFiles[request.DestinationPath] = content;
        }

        return Task.CompletedTask;
    }

    public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var state = GetAliveState(handle);
            if (state.InFlight.TryGetValue(executionId, out var execution))
            {
                execution.Completion.TrySetResult(false);
            }
        }

        return Task.CompletedTask;
    }

    public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_sandboxes.TryGetValue(handle.SandboxId, out var state))
            {
                TerminateLocked(state);
                _sandboxes.Remove(handle.SandboxId);
            }
        }

        return Task.CompletedTask;
    }

    private SandboxState? FindAliveByKey(SandboxAttachKey attachKey)
    {
        return _sandboxes.Values.FirstOrDefault(state => state.Alive && state.Handle.AttachKey == attachKey);
    }

    private void EvictOwnerConflicts(SandboxAttachKey attachKey)
    {
        var conflicts = _sandboxes
                        .Where(entry => string.Equals(entry.Value.Handle.AttachKey.NodeId, attachKey.NodeId, StringComparison.Ordinal)
                                        && !string.Equals(entry.Value.Handle.AttachKey.OwnerUserId, attachKey.OwnerUserId, StringComparison.Ordinal))
                        .Select(entry => entry.Key)
                        .ToArray();

        foreach (var sandboxId in conflicts)
        {
            TerminateLocked(_sandboxes[sandboxId]);
            _sandboxes.Remove(sandboxId);
        }
    }

    private static void TerminateLocked(SandboxState state)
    {
        state.Alive = false;
        foreach (var execution in state.InFlight.Values)
        {
            execution.Completion.TrySetResult(false);
        }

        state.InFlight.Clear();
        state.SandboxFiles.Clear();
    }

    private SandboxState GetAliveState(SandboxHandle handle)
    {
        if (_sandboxes.TryGetValue(handle.SandboxId, out var state) && state.Alive)
        {
            return state;
        }

        throw new SandboxHandleInvalidException($"Sandbox '{handle.SandboxId}' is no longer available.");
    }

    private ScriptedCommand ResolveScript(SandboxCommandRequest request)
    {
        var key = BuildCommandKey(request);
        return _scripts.TryGetValue(key, out var scripted)
            ? scripted
            : new ScriptedCommand(false, 0, string.Empty, string.Empty);
    }

    private static string BuildCommandKey(SandboxCommandRequest request)
    {
        return request.Arguments.Count == 0
            ? request.Executable
            : request.Executable + " " + string.Join(" ", request.Arguments);
    }

    private SandboxCommandResult BuildResult(SandboxCommandRequest request, ScriptedCommand scripted, bool completed, DateTimeOffset startedAt)
    {
        return new SandboxCommandResult
        {
            ExecutionId = request.ExecutionId,
            ExitCode = completed ? scripted.ExitCode : -1,
            StandardOutput = completed ? scripted.StandardOutput : string.Empty,
            StandardError = completed ? scripted.StandardError : "Command was cancelled before completion.",
            Completed = completed,
            Duration = _timeProvider.GetUtcNow() - startedAt
        };
    }

    private void RemoveInFlight(string sandboxId, string executionId, InFlightExecution expected)
    {
        lock (_sync)
        {
            if (_sandboxes.TryGetValue(sandboxId, out var state)
                && state.InFlight.TryGetValue(executionId, out var actual)
                && ReferenceEquals(actual, expected))
            {
                state.InFlight.Remove(executionId);
            }
        }
    }

    private sealed class SandboxState
    {
        public SandboxState(SandboxHandle handle)
        {
            Handle = handle;
        }

        public SandboxHandle Handle { get; }

        public bool Alive { get; set; } = true;

        public Dictionary<string, string> SandboxFiles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, InFlightExecution> InFlight { get; } = new(StringComparer.Ordinal);
    }

    private sealed class InFlightExecution
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ScriptedCommand(bool Blocks, int ExitCode, string StandardOutput, string StandardError);
}
