namespace XE_Local_AI_Engine.Client.Services.Sandbox.Fake;

using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
///     Deterministic, in-memory <see cref="ISandboxRuntimeProvider" /> used as the CI-mandatory provider and as the
///     default until a real provider ships. It needs no Docker and no network:
///     a virtual filesystem backs copy/read, command results are scripted, and a "blocking" command lets
///     cancellation and kill be exercised honestly. All timestamps come from the injected <see cref="TimeProvider" />
///     so behavior is reproducible. Mirrors the production-resident, config-selected <c>FakeDockerRuntimeClient</c>.
/// </summary>
// Serves BOTH per-feature roles, so a test host — and the CI-mandatory default — can drive AgentHome, Coder and
// Development Mode off one deterministic provider instance.
public sealed class FakeSandboxRuntimeProvider : IAgentSandboxRuntimeProvider, IDevelopmentSandboxRuntimeProvider, IWorkSessionSandboxRuntimeProvider
{
    /// <summary>The provider name this fake registers under for configuration-bound selection.</summary>
    public const string Name = "fake";

    private readonly List<SandboxCommandRequest> _executed = [];
    private readonly Dictionary<string, string> _hostFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SandboxState> _sandboxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptedCommand> _scripts = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private int _sandboxCounter;

    public FakeSandboxRuntimeProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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

    public string ProviderName => Name;

    // SupportsTrustedHostWorkspace is advertised because the container work needs unit coverage of callers
    // that bind an engine-managed workspace, and a fake that refused the flag would force every such test onto a real
    // daemon — which is precisely the coverage this fake exists to add *in addition to* real-daemon tests, not instead of them. The
    // fake honours it in the only way an in-memory sandbox can: CreateOrAttachAsync accepts the binding, and the
    // virtual filesystem is preserved across attach exactly as the contract requires of a real one.
    public SandboxProviderCapabilities Capabilities =>
        SandboxProviderCapabilities.SupportsCopyInto
        | SandboxProviderCapabilities.SupportsCopyOut
        | SandboxProviderCapabilities.SupportsCommandCancellation
        | SandboxProviderCapabilities.SupportsAttach
        | SandboxProviderCapabilities.SupportsKill
        | SandboxProviderCapabilities.SupportsTrustedHostWorkspace;

    // ---- ISandboxRuntimeProvider ----

    public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // An in-memory sandbox has no mount namespace and never will. Rejecting rather than ignoring keeps the fake
        // honest in the same direction as the real providers: a caller that asked for a filesystem boundary and was
        // handed a sandbox without one would go on believing it had the boundary, which is precisely the failure the
        // fail-closed capability contract exists to prevent — and a fake that quietly accepted it would hide exactly
        // that bug in every test that used it.
        if (request.Isolation == SandboxIsolationMode.Filesystem)
        {
            throw new SandboxCapabilityNotSupportedException(
                "The fake sandbox provider has no mount namespace and cannot honor SandboxIsolationMode.Filesystem.");
        }

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
                ManifestVersion = request.AttachKey.ManifestVersion,
                Mounts = ResolveIdentityMounts(request)
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
            return BuildResult(request, scripted, completed: true, startedAt);
        }

        using var registration = cancellationToken.Register(static state => ((InFlightExecution)state!).Completion.TrySetCanceled(CancellationToken.None),
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

    public Task ResetDirectoryAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);
        cancellationToken.ThrowIfCancellationRequested();

        var prefix = sandboxPath.EndsWith('/') ? sandboxPath : sandboxPath + "/";
        lock (_sync)
        {
            var state = GetAliveState(handle);
            foreach (var path in state.SandboxFiles.Keys
                                      .Where(path => string.Equals(path, sandboxPath, StringComparison.Ordinal)
                                                     || path.StartsWith(prefix, StringComparison.Ordinal))
                                      .ToArray())
            {
                _ = state.SandboxFiles.Remove(path);
            }
        }

        return Task.CompletedTask;
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

    public async Task<string> ReadFileAsync(SandboxHandle handle,
        string sandboxPath,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var content = await ReadFileAsync(handle, sandboxPath, cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(content) > maxBytes)
        {
            throw new InvalidDataException("The sandbox file exceeds the requested read bound.");
        }

        return content;
    }

    /// <summary>
    ///     Lists the virtual filesystem's entries beneath the requested directory, in the same <c>./relative/path</c>
    ///     shape the real provider emits — so a test that drives Coder or AgentHome through this provider is asserting
    ///     the shape production produces, not a fake one.
    /// </summary>
    public Task<IReadOnlyList<string>> ListFilesAsync(SandboxHandle handle,
        SandboxListFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            IReadOnlyList<string> entries =
            [
                .. EnumerateUnder(handle, request.DirectoryPath, cancellationToken)
                   .Where(entry => request.IsPathSuppressed?.Invoke(entry.Relative) != true)
                   .Where(entry => request.NameGlob is not { Length: > 0 } glob
                                   || FileSystemName.MatchesSimpleExpression(glob, entry.Relative.Split('/')[^1], ignoreCase: true))
                   .Select(static entry => "./" + entry.Relative)
                   .Take(Math.Max(request.MaxEntries, val2: 0))
            ];
            return Task.FromResult(entries);
        }
    }

    /// <inheritdoc cref="ISandboxRuntimeProvider.SearchTextAsync" />
    public Task<IReadOnlyList<string>> SearchTextAsync(SandboxHandle handle,
        SandboxSearchTextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            var matches = new List<string>();
            if (request.Pattern.Length == 0)
            {
                return Task.FromResult<IReadOnlyList<string>>(matches);
            }

            var expression = request.IsRegex
                ? new Regex(request.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250))
                : null;

            foreach (var (relative, content) in EnumerateUnder(handle, request.DirectoryPath, cancellationToken))
            {
                if (request.IsPathSuppressed?.Invoke(relative) == true)
                {
                    continue;
                }

                var lineNumber = 0;
                foreach (var line in content.Split('\n'))
                {
                    lineNumber++;
                    var text = line.TrimEnd('\r');
                    var hit = expression is null ? text.Contains(request.Pattern, StringComparison.Ordinal) : expression.IsMatch(text);
                    if (!hit)
                    {
                        continue;
                    }

                    matches.Add(string.Create(CultureInfo.InvariantCulture, $"./{relative}:{lineNumber}:{text}"));
                    if (matches.Count >= request.MaxMatches)
                    {
                        return Task.FromResult<IReadOnlyList<string>>(matches);
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(matches);
        }
    }

    // The virtual filesystem is keyed by sandbox-absolute path, so "under this directory" is a prefix test — and the
    // directory itself is addressed the way every other operation addresses one.
    private IEnumerable<VirtualFile> EnumerateUnder(SandboxHandle handle,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        var prefix = directoryPath.TrimEnd('/') + "/";
        return state.SandboxFiles
                    .Where(file => file.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(static file => file.Key, StringComparer.Ordinal)
                    .Select(file => new VirtualFile(file.Key[prefix.Length..], file.Value));
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
            _scripts[commandLine] = new ScriptedCommand(Blocks: false, exitCode, standardOutput, standardError);
        }
    }

    /// <summary>Register a command that blocks until it is cancelled or the sandbox is killed (for cancel/kill tests).</summary>
    public void RegisterBlockingCommand(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        lock (_sync)
        {
            _scripts[commandLine] = new ScriptedCommand(Blocks: true, ExitCode: 0, string.Empty, string.Empty);
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
    ///     Mirrors <c>ProcessSandboxRuntimeProvider</c>'s identity map, because that is the behaviour a caller written
    ///     against the fake has to keep when it meets the process provider. It does not check the host filesystem: the
    ///     fake's whole point is a virtual one, and a unit test naming a directory it never created must still get a
    ///     usable handle.
    /// </summary>
    private static IReadOnlyList<SandboxMountBinding> ResolveIdentityMounts(SandboxCreateRequest request)
    {
        var bindings = new List<SandboxMountBinding>();
        if (request.TrustedHostWorkspace is not null)
        {
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.TrustedHostWorkspace.RootPath));
            bindings.Add(new SandboxMountBinding(workspace, workspace, ReadOnly: false));
        }

        foreach (var mount in request.Mounts ?? [])
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount.HostPath));
            bindings.Add(new SandboxMountBinding(canonical, canonical, mount.ReadOnly));
        }

        return bindings;
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
            : new ScriptedCommand(Blocks: false, ExitCode: 0, string.Empty, string.Empty);
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

    // One entry of the virtual filesystem as seen from the enumerated directory: its path relative to that directory,
    // and the file's whole content.
    private sealed record VirtualFile(string Relative, string Content);
}
