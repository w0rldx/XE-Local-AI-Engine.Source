namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

/// <summary>
///     The <c>process</c> sandbox <see cref="ISandboxRuntimeProvider" />: backs AgentHome with a supervised child
///     <see cref="Process" /> rooted at a node-scoped working-directory jail on the worker host. It owns no Docker, no
///     gRPC, and no HostAgent — it is the drop-in successor to the container provider, holding the exact same
///     provider-neutral contract so AgentHome's copy → run → export → apply loop is byte-behavior-identical. All
///     soft-guard logic (working-dir jail, path canonicalization, no-follow open, byte budgets, timeout, tree-kill)
///     lives INSIDE this class; a future hardware-isolated (MXC) provider replaces the whole provider, not the
///     contract.
///     <para>
///         Security posture (v1): this is supervised execution, NOT an OS isolation boundary. There is NO network
///         isolation and NO CPU/memory/PID enforcement for sandbox processes; a request that asks for either is
///         rejected fail-closed (<see cref="SandboxCapabilityNotSupportedException" />) rather than silently ignored.
///         What IS enforced: the working-directory jail, path/symlink guards, a scrubbed child environment (the
///         worker's secret-bearing environment is NOT inherited — only a fixed system/toolchain allow-list is
///         forwarded), the per-command timeout, tree-kill, and captured-output byte caps. The single-user local-node
///         threat model accepts the absent isolation — risky execution is approval-gated upstream — and hardware
///         isolation plus a no-network mechanism are deferred to a future MXC provider behind this same seam. The
///         provider does NOT claim best-effort no-network because it implements no mechanism for it.
///     </para>
/// </summary>
public sealed class ProcessSandboxRuntimeProvider : ISandboxRuntimeProvider, IDisposable
{
    /// <summary>The provider name this registers under for configuration-bound selection.</summary>
    public const string Name = "process";

    // Default captured-output ceiling per stream (stdout / stderr). Mirrors the container provider's bounded transfer
    // posture: capture is capped, and reading stops once the cap is reached so a runaway command cannot exhaust memory.
    private const int DefaultMaxCapturedOutputBytes = 4 * 1024 * 1024;

    // O_RDONLY (0x0) | O_NOFOLLOW (0x20000) | O_CLOEXEC (0x80000) on Linux — the same flag set the deleted container
    // provider used. A raw (FileOptions) cast for O_NOFOLLOW throws, so the libc open() DllImport below is required
    // (parity with the AgentHome marker-J-local host-file no-follow guard).
    private const int ReadOnlyNoFollowCloseOnExecFlags = 0x0 | 0x20000 | 0x80000;

    // O_WRONLY (0x1) | O_CREAT (0x40) | O_TRUNC (0x200) | O_NOFOLLOW (0x20000) | O_CLOEXEC (0x80000) on Linux. A
    // no-follow create fails with ELOOP if the leaf already exists as a symlink, so the copy-into write cannot be
    // redirected through a planted leaf symlink. 0o644 mode for the created file.
    private const int WriteCreateNoFollowCloseOnExecFlags = 0x1 | 0x40 | 0x200 | 0x20000 | 0x80000;
    private const int DefaultCreateFileMode = 0b110_100_100;

    // SECURITY INVARIANT: a sandboxed child NEVER inherits the worker process environment. The worker holds secrets
    // (cloud API keys, OAuth tokens, the node SQLite key, connection strings) as environment variables; forwarding the
    // whole environment would hand every one of them to arbitrary sandbox commands. Instead the child starts from an
    // EMPTY environment and is repopulated only from this fixed allow-list of system/toolchain variables — the minimum
    // the fixed production executables (`dotnet --version`, `git`, `find`, `grep`) need to run on Linux and Windows —
    // after which the caller's explicit request.Environment is layered on top. No secret-bearing variable appears here.
    // Names absent on the current OS are simply skipped; lookup is OS-correct (case-insensitive on Windows).
    private static readonly string[] InheritableEnvironmentAllowlist =
    [
        // Executable resolution + user/home + temp — needed on both platforms.
        "PATH",
        "HOME",
        "TMPDIR",
        "TMP",
        "TEMP",
        // Locale so tool output text is well-formed.
        "LANG",
        "LC_ALL",
        // .NET host location + telemetry/logo suppression (no network, no prompt). DOTNET_ROOT lets the muxer find the
        // runtime when dotnet is installed off a default path (e.g. a version manager).
        "DOTNET_ROOT",
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_NOLOGO",
        // Windows essentials: most Win32 processes fail to start without SystemRoot/ComSpec; PATHEXT resolves
        // executable extensions; the profile/AppData vars carry the git and NuGet/.NET per-user config the tools read.
        "SystemRoot",
        "windir",
        "SystemDrive",
        "ComSpec",
        "PATHEXT",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "APPDATA",
        "LOCALAPPDATA"
    ];

    private readonly string _jailRoot;
    private readonly ILogger<ProcessSandboxRuntimeProvider> _logger;

    private readonly long _maxCopyFileBytes;
    private readonly ConcurrentDictionary<string, JailState> _sandboxes = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    // The logger is optional so tests can construct the provider directly; ActivatorUtilities injects it in production.
    public ProcessSandboxRuntimeProvider(IOptions<LocalContainerOptions> copyOptions,
        TimeProvider timeProvider,
        ILogger<ProcessSandboxRuntimeProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(copyOptions);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<ProcessSandboxRuntimeProvider>.Instance;

        // Reuse the existing per-file copy ceiling so the jail's byte-cap-on-re-read matches the container provider's
        // (64 MiB default). No new options type is introduced this phase.
        _maxCopyFileBytes = copyOptions.Value.MaxCopyFileBytes;

        // A worker-local jail container directory owned by this provider instance. The provider is a DI singleton, so
        // there is exactly one container root per running worker process; the instance suffix keeps two providers (e.g.
        // concurrent tests, or a restart racing teardown) from colliding on each other's node-scoped jails. Each
        // node-scoped sandbox is a subdirectory under it, named deterministically from the attach key.
        _jailRoot = Path.Combine(Path.GetTempPath(), "xe-agent-home-sandboxes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jailRoot);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        // Dispose = ensure tree-kill of any live process the provider still supervises. Best-effort: a sandbox can
        // already be torn down.
        foreach (var state in _sandboxes.Values)
        {
            TerminateState(state);
        }

        _sandboxes.Clear();

        // Remove this instance's container root (all node-scoped jails already deleted by TerminateState).
        try
        {
            if (Directory.Exists(_jailRoot))
            {
                Directory.Delete(_jailRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort teardown.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort teardown.
        }
    }

    public string ProviderName => Name;

    public SandboxProviderCapabilities Capabilities =>
        // Serves: copy-into / copy-out (local FS within the jail), per-command cancellation (tree-kill), attach
        // (reattach by key), kill (tree-kill + invalidate). NOT served: read-only mounts (no mount layer), network
        // policy (no isolation mechanism in v1 — see the class doc and decision D-1), and resource limits — the
        // SandboxResourceLimits record (CPU/memory/PID ceilings) cannot be enforced, so this provider does NOT advertise
        // SupportsResourceLimits. Because those two guarantees are unenforceable, CreateOrAttachAsync now REJECTS a
        // request that asks for them (fail-closed) rather than ignoring them. (The per-command timeout and the output
        // byte cap ARE enforced, but those are not what that flag covers.) follow-up: enforce CPU/mem/PID via pre-exec
        // rlimit (RLIMIT_AS/CPU/NPROC) post-RC.
        SandboxProviderCapabilities.SupportsCopyInto
        | SandboxProviderCapabilities.SupportsCopyOut
        | SandboxProviderCapabilities.SupportsCommandCancellation
        | SandboxProviderCapabilities.SupportsAttach
        | SandboxProviderCapabilities.SupportsKill
        | SandboxProviderCapabilities.SupportsTrustedHostWorkspace;

    public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail-closed capability contract: this provider supervises but does not ISOLATE. It cannot confine network
        // egress and cannot enforce a CPU/memory/PID ceiling. Rather than silently accept such a request and return a
        // sandbox weaker than the caller asked for, reject it up front so a caller can never believe it received an
        // isolation guarantee the provider does not implement. The only network posture it can honestly serve is
        // Unrestricted (the child shares the host network); any resource limit is unenforceable and likewise rejected.
        RejectUnenforceableGuarantees(request);

        lock (_sync)
        {
            var attached = FindAliveByKey(request.AttachKey);
            if (attached is not null)
            {
                EnsureCompatibleWorkspaceBinding(attached, request.TrustedHostWorkspace);
                return Task.FromResult(attached.Handle);
            }

            // Owner change on the same node forbids reuse: kill and remove any sandbox keyed to the same node under a
            // different owner before creating the new one (parity with FakeSandboxRuntimeProvider.EvictOwnerConflicts).
            EvictOwnerConflicts(request.AttachKey);

            var sandboxId = BuildSandboxId(request.AttachKey);
            var jailDirectory = request.TrustedHostWorkspace is null
                ? Path.Combine(_jailRoot, sandboxId)
                : ResolveTrustedHostWorkspace(request.TrustedHostWorkspace.RootPath);
            Directory.CreateDirectory(jailDirectory);

            var handle = new SandboxHandle
            {
                ProviderName = Name,
                SandboxId = sandboxId,
                AttachKey = request.AttachKey,
                CreatedAt = _timeProvider.GetUtcNow(),
                ManifestVersion = request.AttachKey.ManifestVersion
            };
            _sandboxes[sandboxId] = new JailState(handle, jailDirectory, request.TrustedHostWorkspace is not null);
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

        var state = GetAliveState(handle);
        var startedAt = _timeProvider.GetUtcNow();

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = ResolveWorkingDirectory(state, request.WorkingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            UseShellExecute = false
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Start the child from a scrubbed environment: ProcessStartInfo pre-seeds Environment with the FULL parent
        // (worker) environment, so clear it and repopulate only the allow-listed system/toolchain variables. This is the
        // load-bearing anti-leak control — a sandbox command must never observe the worker's secret-bearing variables.
        startInfo.Environment.Clear();
        foreach (var name in InheritableEnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        // Layer the caller's explicit request environment on top of the allow-list (it may override or add keys the
        // command genuinely needs). The caller is trusted node code composing a fixed command, not the sandboxed child.
        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        // Capture stdout/stderr via the event pump with a hard per-stream byte budget. Reading stops appending past the
        // cap so a runaway command cannot exhaust memory, while the pump keeps draining the pipe so the child never
        // blocks on a full buffer.
        var standardOutputBuilder = new CappedStringBuilder(DefaultMaxCapturedOutputBytes);
        var standardErrorBuilder = new CappedStringBuilder(DefaultMaxCapturedOutputBytes);
        process.OutputDataReceived += (_, eventArgs) => standardOutputBuilder.AppendLine(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => standardErrorBuilder.AppendLine(eventArgs.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            // The executable could not be launched (not found / not executable). Surface a non-completed result rather
            // than throwing, so the AgentHome run flow records a failed command the same way a non-zero exit does.
            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardError = "The sandbox command could not be launched.",
                Completed = false,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }

        // A per-command source that best-effort cancel (CancelCommandAsync) and sandbox kill (KillAsync) fire. Its
        // firing yields a non-throwing Completed=false result — parity with the fake's CancelCommandAsync — distinct
        // from a caller-token cancel (which throws) and a timeout (which returns a timed-out result).
        var commandCancelSource = new CancellationTokenSource();
        var inFlight = new InFlightExecution(process, commandCancelSource);
        if (!state.InFlight.TryAdd(request.ExecutionId, inFlight))
        {
            // Another command is already in flight under this execution id; kill the just-started one and reject.
            TreeKill(process);
            process.Dispose();
            commandCancelSource.Dispose();
            throw new InvalidOperationException($"Execution id '{request.ExecutionId}' is already in flight for this sandbox.");
        }

        // A timeout (not a caller cancel) yields a non-throwing TimedOut result; a caller cancel propagates
        // OperationCanceledException; a best-effort command cancel yields a non-throwing Completed=false result.
        using var timeoutSource = new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, commandCancelSource.Token);
        if (request.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            timeoutSource.CancelAfter(timeout);
        }

        try
        {
            if (startInfo.RedirectStandardInput && request.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), linkedSource.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            // WaitForExitAsync also waits for the async output pump to drain, so the captured builders are complete
            // once it returns. The linked token unblocks the wait on a cancel/timeout.
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);

            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = process.ExitCode,
                StandardOutput = standardOutputBuilder.ToString(),
                StandardError = standardErrorBuilder.ToString(),
                StandardOutputTruncated = standardOutputBuilder.IsTruncated,
                StandardErrorTruncated = standardErrorBuilder.IsTruncated,
                Completed = true,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }
        catch (OperationCanceledException) when (commandCancelSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A best-effort CancelCommandAsync (or a sandbox kill) fired: tree-kill and return a non-throwing
            // Completed=false result so AgentHome treats it like the fake's cancelled command, not a caller cancel.
            TreeKill(process);
            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardError = "Command was cancelled before completion.",
                Completed = false,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Per-command timeout fired while the caller token stayed un-cancelled: kill the tree and return a
            // non-throwing timed-out result (Completed=false / ExitCode=-1), matching the container/fake timeout shape.
            TreeKill(process);
            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardError = "Command timed out.",
                Completed = false,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }
        catch (OperationCanceledException)
        {
            // A caller cancel tree-kills and propagates OperationCanceledException so AgentHomeService can disambiguate
            // caller-cancel from timeout.
            TreeKill(process);
            throw;
        }
        finally
        {
            _ = state.InFlight.TryRemove(request.ExecutionId, out _);
            process.Dispose();
            commandCancelSource.Dispose();
        }
    }

    public async Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        var destination = ResolveJailPath(state, request.DestinationPath);

        // SECURITY (hard reject): a sandboxed command can plant a symlink inside the jail, so the destination's parent
        // chain — and the leaf if it already exists — must contain no symlink that would redirect the write outside the
        // jail. The parent dirs are created first so they exist (and are re-checked) before the no-follow create.
        var parent = Path.GetDirectoryName(destination);
        if (parent is not null)
        {
            // Validate the existing prefix BEFORE Directory.CreateDirectory: that API follows an intermediate
            // symlink, so creating first could mutate an outside directory before the later rejection. Re-check after
            // creation to cover every newly materialized component and a concurrent swap.
            EnsureNoSymlinkComponentsUnderJail(state.JailRoot, parent, request.DestinationPath);
            Directory.CreateDirectory(parent);
            EnsureNoSymlinkComponentsUnderJail(state.JailRoot, parent, request.DestinationPath);
        }

        EnsureNoSymlinkComponentsUnderJail(state.JailRoot, destination, request.DestinationPath);

        // Re-open the host source under the no-follow / byte-cap-on-re-read guard ported from the container provider:
        // never trust a path string sized by an earlier walk — a swap-to-symlink throws (security), while an over-cap or
        // grown-after-sizing file is SKIPPED-AND-LOGGED (degrade gracefully, parity with the deleted provider) so one
        // legitimately-large file does not abort the whole workspace-copy loop in AgentHomeWorkspaceService.
        var content = ReadHostFileUnderGuard(request.SourcePath);
        if (content is null)
        {
            // AgentHomeWorkspaceService copies plan.Files in a loop with NO per-file try/catch and only a folder-wide
            // MaxSelectedFolderBytes pre-check (no per-file MaxCopyFileBytes gate), so a single >cap file reaching here
            // MUST NOT throw or it aborts the entire folder copy. Skip+log, matching the deleted
            // LocalContainerSandboxProvider. Security cases (traversal/symlink) still throw above.
            _logger.LogWarning("Copy-into skipped: a selected file exceeded the {Cap}-byte per-file cap or grew after sizing on re-read.",
                _maxCopyFileBytes);
            return;
        }

        // No-follow create on Linux: if the leaf was swapped for a symlink between the component check and the write,
        // O_NOFOLLOW makes the create fail rather than write through the link.
        await WriteJailFileNoFollowAsync(destination, content, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
    {
        return await ReadFileAsync(handle, sandboxPath, int.MaxValue, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadFileAsync(SandboxHandle handle,
        string sandboxPath,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        var resolved = ResolveJailPath(state, sandboxPath);

        // SECURITY: reject any symlink component (a sandboxed command can plant one), then read through a no-follow
        // open so a leaf swapped to a symlink after the component check cannot redirect the read outside the jail.
        EnsureNoSymlinkComponentsUnderJail(state.JailRoot, resolved, sandboxPath);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"Sandbox path '{sandboxPath}' was not found.", sandboxPath);
        }

        var bytes = await ReadJailFileBytesNoFollowAsync(resolved, maxBytes, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetAliveState(handle);
        var source = ResolveJailPath(state, request.SourcePath);

        // SECURITY: reject any symlink component on the jail-side source, then read through a no-follow open so an
        // escaping symlink cannot copy a host file outside the jail out to the caller's destination.
        EnsureNoSymlinkComponentsUnderJail(state.JailRoot, source, request.SourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Sandbox path '{request.SourcePath}' was not found.", request.SourcePath);
        }

        // Read the raw bytes from inside the jail and write them to the host destination so a binary artifact survives
        // the round trip unchanged (parity with the container provider's copy-out).
        var content = await ReadJailFileBytesNoFollowAsync(source, int.MaxValue, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(request.DestinationPath, content, cancellationToken).ConfigureAwait(false);
    }

    public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        cancellationToken.ThrowIfCancellationRequested();

        // Best-effort: cancel + tree-kill the in-flight command by execution id. Firing the command-cancel source makes
        // the in-flight ExecuteAsync return a non-throwing Completed=false result. A missing id or already-gone sandbox
        // is a no-op (parity with the container/fake providers).
        if (_sandboxes.TryGetValue(handle.SandboxId, out var state)
            && state.InFlight.TryGetValue(executionId, out var inFlight))
        {
            inFlight.RequestCancel();
        }

        return Task.CompletedTask;
    }

    public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_sandboxes.TryRemove(handle.SandboxId, out var state))
            {
                TerminateState(state);
            }
        }

        return Task.CompletedTask;
    }

    // ---- jail / state helpers ----

    private static void RejectUnenforceableGuarantees(SandboxCreateRequest request)
    {
        // Network: this provider isolates nothing, so the child shares the host network. Unrestricted is the only
        // honest posture; None (no network) and Restricted (egress allow-list) demand isolation it cannot deliver.
        if (request.NetworkPolicy != SandboxNetworkPolicy.Unrestricted)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{Name}' sandbox provider does not isolate network egress and cannot honor NetworkPolicy '{request.NetworkPolicy}'. It supervises but does not isolate; only NetworkPolicy.Unrestricted is supported. Use an OS-isolated provider to enforce a network policy."));
        }

        // Resource limits: no cgroup / job-object ceiling is applied, so any requested CPU/memory/PID cap is
        // unenforceable. Reject rather than silently run without the ceiling the caller asked for.
        var limits = request.ResourceLimits;
        if (limits is not null && (limits.CpuCount.HasValue || limits.MemoryMb.HasValue || limits.PidsLimit.HasValue))
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{Name}' sandbox provider does not enforce resource limits (CPU/memory/PID). Remove SandboxResourceLimits or use an OS-isolated provider that advertises SupportsResourceLimits."));
        }
    }

    private JailState? FindAliveByKey(SandboxAttachKey attachKey)
    {
        return _sandboxes.Values.FirstOrDefault(state => state.Alive && state.Handle.AttachKey == attachKey);
    }

    private static void EnsureCompatibleWorkspaceBinding(JailState state, SandboxTrustedHostWorkspace? requested)
    {
        if (requested is null)
        {
            if (state.PreserveJailRoot)
            {
                throw new SandboxHandleInvalidException("The existing sandbox is bound to a trusted host workspace, but the attach request is not.");
            }

            return;
        }

        var requestedRoot = ResolveTrustedHostWorkspace(requested.RootPath);
        if (!state.PreserveJailRoot || !string.Equals(requestedRoot, state.JailRoot, StringComparison.Ordinal))
        {
            throw new SandboxHandleInvalidException("The existing sandbox is bound to a different trusted host workspace.");
        }
    }

    private static string ResolveTrustedHostWorkspace(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var canonical = Path.GetFullPath(rootPath);
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException("The trusted host workspace must be an existing canonical directory.");
        }

        EnsureNoSymbolicLinkComponents(canonical);

        return canonical;
    }

    private static void EnsureNoSymbolicLinkComponents(string canonicalPath)
    {
        var root = Path.GetPathRoot(canonicalPath)
                   ?? throw new UnauthorizedAccessException("The trusted host workspace must have a rooted canonical path.");
        var current = root;
        foreach (var segment in canonicalPath[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.ResolveLinkTarget(current, returnFinalTarget: false) is not null)
            {
                throw new UnauthorizedAccessException("A trusted host workspace path cannot contain symbolic links.");
            }
        }
    }

    private void EvictOwnerConflicts(SandboxAttachKey attachKey)
    {
        var conflicts = _sandboxes
                        .Where(entry => string.Equals(entry.Value.Handle.AttachKey.NodeId, attachKey.NodeId, StringComparison.Ordinal)
                                        && !string.Equals(entry.Value.Handle.AttachKey.OwnerUserId, attachKey.OwnerUserId, StringComparison.Ordinal))
                        .Select(entry => entry.Key)
                        .ToList();

        foreach (var sandboxId in conflicts)
        {
            if (_sandboxes.TryRemove(sandboxId, out var state))
            {
                TerminateState(state);
            }
        }
    }

    private static string BuildSandboxId(SandboxAttachKey attachKey)
    {
        // Hash the complete attach scope. Owner/node alone is insufficient because AgentHome and Development may use
        // different runtime profiles or manifest versions for the same logical node and must coexist without one
        // dictionary entry overwriting the other.
        var scope = string.Concat(attachKey.OwnerUserId, "\0",
            attachKey.NodeId, "\0",
            attachKey.ProviderName, "\0",
            attachKey.RuntimeProfile, "\0",
            attachKey.ManifestVersion.ToString(CultureInfo.InvariantCulture));
        var scopeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..16];
        var nodeSegment = SanitizeSegment(attachKey.NodeId);
        return string.Create(CultureInfo.InvariantCulture, $"process-{nodeSegment}-{scopeHash}");
    }

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        return builder.Length == 0 ? "node" : builder.ToString();
    }

    private JailState GetAliveState(SandboxHandle handle)
    {
        if (_sandboxes.TryGetValue(handle.SandboxId, out var state) && state.Alive)
        {
            return state;
        }

        throw new SandboxHandleInvalidException($"Sandbox '{handle.SandboxId}' is no longer available.");
    }

    private static string ResolveWorkingDirectory(JailState state, string? requestedWorkingDirectory)
    {
        if (requestedWorkingDirectory is null)
        {
            return state.JailRoot;
        }

        var canonicalPath = ResolveJailPath(state, requestedWorkingDirectory);
        EnsureNoSymlinkComponentsUnderJail(state.JailRoot, canonicalPath, requestedWorkingDirectory);
        return canonicalPath;
    }

    /// <summary>
    ///     Canonicalizes a (possibly sandbox-absolute) path into a host path that MUST live under the jail root. Any
    ///     path that escapes the jail — via <c>..</c> traversal or an absolute path outside it — is rejected lexically
    ///     (<see cref="Path.GetFullPath(string)" /> collapses <c>..</c>). This is the load-bearing jail control. It does
    ///     NOT resolve symlinks; a path under the jail can still TRAVERSE a planted symlink (a command running with the
    ///     jail as CWD can create one). The caller must additionally pass the canonical path through
    ///     <see cref="EnsureNoSymlinkComponentsUnderJail" /> (read/write legs) before opening to close that escape.
    /// </summary>
    private static string ResolveJailPath(JailState state, string sandboxPath)
    {
        // AgentHome addresses files with sandbox-absolute paths (e.g. /agent-home/workspace/...). Treat a leading
        // separator as jail-relative so an absolute sandbox path maps under the jail rather than at the host root.
        var relative = sandboxPath.TrimStart('/', '\\');
        var combined = Path.Combine(state.JailRoot, relative);
        var canonical = Path.GetFullPath(combined);

        if (!IsUnderJailRoot(state.JailRoot, canonical))
        {
            throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' escapes the jail and is rejected.");
        }

        return canonical;
    }

    private static bool IsUnderJailRoot(string jailRoot, string canonicalPath)
    {
        var jailPrefix = jailRoot.EndsWith(Path.DirectorySeparatorChar)
            ? jailRoot
            : jailRoot + Path.DirectorySeparatorChar;

        return string.Equals(canonicalPath, jailRoot, StringComparison.Ordinal)
               || canonicalPath.StartsWith(jailPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Rejects a jail-relative path whose final component, or ANY component between the jail root and the leaf, is a
    ///     symlink (reparse point). A command running with the jail as its working directory can legitimately plant a
    ///     symlink inside the jail (e.g. <c>workspace/x -&gt; /etc</c>); the lexical <see cref="ResolveJailPath" /> check
    ///     passes such a path, but opening through it would read/write OUTSIDE the jail. Walking each component with a
    ///     no-follow probe (<see cref="File.ResolveLinkTarget(string, bool)" /> returns non-null for a symlink) closes
    ///     that escape for both the read legs and the copy-into write. <paramref name="canonicalPath" /> must already be
    ///     proven under the jail by <see cref="ResolveJailPath" />. Only existing components are probed (a not-yet-created
    ///     copy-into leaf cannot be a symlink). Throws <see cref="UnauthorizedAccessException" /> on the first symlink
    ///     component — a swap/plant-after-resolve escape signal.
    /// </summary>
    private static void EnsureNoSymlinkComponentsUnderJail(string jailRoot, string canonicalPath, string sandboxPath)
    {
        // Walk from the leaf upward; stop at the jail root (the jail root itself is trusted — it is created by this
        // provider, not by a sandboxed command).
        var jailRootFull = Path.GetFullPath(jailRoot);
        var current = canonicalPath;
        while (!string.Equals(current, jailRootFull, StringComparison.Ordinal))
        {
            // A component above the jail root means the path escaped (defense in depth; ResolveJailPath already
            // rejected escapes, but never walk past the trusted boundary).
            if (!IsUnderJailRoot(jailRootFull, current))
            {
                throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' escapes the jail and is rejected.");
            }

            // Probe only existing components. A symlink (file or directory) returns a non-null link target under a
            // no-follow resolve; a real file/dir or a not-yet-created leaf returns null.
            if ((File.Exists(current) || Directory.Exists(current))
                && File.ResolveLinkTarget(current, returnFinalTarget: false) is not null)
            {
                throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' traverses or targets a symlink inside the jail and is rejected.");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }
    }

    private static void TerminateState(JailState state)
    {
        lock (state.Sync)
        {
            if (!state.Alive)
            {
                return;
            }

            state.Alive = false;
        }

        foreach (var inFlight in state.InFlight.Values)
        {
            // Signal the in-flight ExecuteAsync that its command was cancelled (Completed=false) AND tree-kill the
            // process so a sandbox kill terminates every running command immediately.
            inFlight.RequestCancel();
            TreeKill(inFlight.Process);
        }

        state.InFlight.Clear();

        try
        {
            if (!state.PreserveJailRoot && Directory.Exists(state.JailRoot))
            {
                Directory.Delete(state.JailRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort jail teardown; a locked file does not fail the kill.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort jail teardown.
        }
    }

    private static void TreeKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // entireProcessTree:true kills descendants too. On Linux the runtime kills the process group; on
                // Windows it walks the tree via the OS APIs. (A dedicated Windows Job Object with
                // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE would be strictly stronger for orphan reaping, but Process.Kill
                // with entireProcessTree is sufficient and OS-correct here; the Job Object polish is deferred and is
                // not load-bearing for the Linux-primary runtime.)
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the check and the kill — nothing to do.
        }
        catch (NotSupportedException)
        {
            // Tree-kill unsupported on this platform; fall back to a single-process kill.
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }
    }

    // ---- ported host-file no-follow / byte-cap guard (from LocalContainerSandboxProvider, marker-J-local pattern) ----

    /// <summary>
    ///     Reads the host file under the no-follow / byte-recheck guards. Returns the bytes, or <see langword="null" />
    ///     when the file exceeds the per-file cap on this re-read or grew after sizing (caller blocks the copy). Throws
    ///     <see cref="UnauthorizedAccessException" /> when the final path component is a symlink or the open cannot be
    ///     performed safely — a swap-after-walk attack signal.
    /// </summary>
    private byte[]? ReadHostFileUnderGuard(string sourcePath)
    {
        var fileHandle = OpenNoFollow(sourcePath);

        using (fileHandle)
        {
            var length = RandomAccess.GetLength(fileHandle);
            if (length > _maxCopyFileBytes)
            {
                return null;
            }

            var content = new byte[length];
            var read = 0;
            while (read < content.Length)
            {
                var chunk = RandomAccess.Read(fileHandle, content.AsSpan(read), read);
                if (chunk == 0)
                {
                    // The file shrank after the length read; copy only what is actually present.
                    return content[..read];
                }

                read += chunk;
            }

            // Growth-after-sizing check: a single probe byte past the sized length means the file grew between the
            // length read and the copy. Block (null) rather than silently truncate to the stale size.
            Span<byte> probe = stackalloc byte[1];
            if (RandomAccess.Read(fileHandle, probe, length) > 0)
            {
                return null;
            }

            return content;
        }
    }

    /// <summary>
    ///     Opens the host file refusing a symlink at the final component. On Linux this is an atomic <c>open(2)</c> with
    ///     <c>O_NOFOLLOW</c> (the kernel fails with <c>ELOOP</c> if the leaf is a symlink), closing the check-then-open
    ///     race a managed <c>lstat</c> + open would leave. A raw <c>(FileOptions)</c> cast for <c>O_NOFOLLOW</c> throws,
    ///     so the libc <c>open()</c> DllImport is required (parity with the AgentHome marker-J-local guard). On a
    ///     non-Linux host this provider is not the primary runtime, so it falls back to a plain open and relies on the
    ///     canonicalized jail check plus the byte re-check. Throws <see cref="UnauthorizedAccessException" /> when the
    ///     leaf is a symlink or the open otherwise fails.
    /// </summary>
    private static SafeFileHandle OpenNoFollow(string sourcePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                return File.OpenHandle(sourcePath);
            }
            catch (IOException exception)
            {
                throw new UnauthorizedAccessException("a selected file could not be opened safely for copy.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new UnauthorizedAccessException("a selected file could not be opened safely for copy (access denied).", exception);
            }
        }

        // Null-terminate the UTF-8 path for libc.
        var pathBytes = new byte[Encoding.UTF8.GetByteCount(sourcePath) + 1];
        Encoding.UTF8.GetBytes(sourcePath, pathBytes);
        var fileDescriptor = open(pathBytes, ReadOnlyNoFollowCloseOnExecFlags);
        if (fileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new UnauthorizedAccessException(string.Create(CultureInfo.InvariantCulture,
                $"a selected file could not be opened safely for copy (it may have been replaced by a link; errno {error})."));
        }

        return new SafeFileHandle(fileDescriptor, ownsHandle: true);
    }

    // A single libc open(). The path is marshalled by the caller into a null-terminated UTF-8 byte array so any
    // filename round-trips correctly; the import takes the raw bytes. DllImport (not source-generated LibraryImport)
    // keeps the project free of AllowUnsafeBlocks — the source generator buys nothing for one call.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags);

    // The 3-arg open() used for O_CREAT (the mode is honored only when the file is created).
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags, int mode);

    /// <summary>
    ///     Reads a jail-side file's raw bytes through a no-follow open. On Linux the open is atomic with
    ///     <c>O_NOFOLLOW</c> so a leaf swapped to a symlink after the per-component check fails the open instead of
    ///     redirecting the read. On a non-Linux host (not the primary runtime) it falls back to a plain handle and
    ///     relies on the per-component symlink check plus the jail canonicalization. Throws
    ///     <see cref="UnauthorizedAccessException" /> when the leaf is a symlink or the open otherwise fails.
    /// </summary>
    private static async Task<byte[]> ReadJailFileBytesNoFollowAsync(string jailPath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var handle = OpenNoFollow(jailPath);
        var length = RandomAccess.GetLength(handle);
        if (length > maxBytes)
        {
            throw new InvalidDataException("The sandbox file exceeds the requested read bound.");
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read), read, cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                return buffer[..read];
            }

            read += chunk;
        }

        Memory<byte> probe = new byte[1];
        if (await RandomAccess.ReadAsync(handle, probe, length, cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new InvalidDataException("The sandbox file grew while it was read under the requested bound.");
        }

        return buffer;
    }

    /// <summary>
    ///     Writes bytes to a jail-side path through a no-follow create. On Linux <c>O_NOFOLLOW</c> makes the create fail
    ///     (ELOOP) if the leaf already exists as a symlink, so a planted leaf symlink cannot redirect the copy-into write
    ///     outside the jail. On a non-Linux host it falls back to a plain create (the per-component symlink check still
    ///     guards intermediate components). Throws <see cref="UnauthorizedAccessException" /> when the leaf is a symlink
    ///     or the create otherwise fails.
    /// </summary>
    private static async Task WriteJailFileNoFollowAsync(string jailPath, byte[] content, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            // Non-Linux fallback: the per-component symlink check already ran; a final-component existing symlink would
            // have been rejected there. Plain write.
            await File.WriteAllBytesAsync(jailPath, content, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pathBytes = new byte[Encoding.UTF8.GetByteCount(jailPath) + 1];
        Encoding.UTF8.GetBytes(jailPath, pathBytes);
        var fileDescriptor = open(pathBytes, WriteCreateNoFollowCloseOnExecFlags, DefaultCreateFileMode);
        if (fileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new UnauthorizedAccessException(string.Create(CultureInfo.InvariantCulture,
                $"the copy-into destination could not be created safely (it may be a symlink; errno {error})."));
        }

        using var handle = new SafeFileHandle(fileDescriptor, ownsHandle: true);
        await RandomAccess.WriteAsync(handle, content, fileOffset: 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     A thread-safe string accumulator with a hard ceiling measured in real UTF-8 BYTES (matching the
    ///     <c>…Bytes</c> budget name). The event pump can fire from a pool thread, so appends lock; once the byte cap is
    ///     reached further data is discarded (the pipe is still drained by the pump so the child never blocks). A
    ///     multibyte line that would cross the cap is truncated at a UTF-8 rune boundary so the accumulated string never
    ///     exceeds <paramref name="capBytes" /> encoded bytes.
    /// </summary>
    private sealed class CappedStringBuilder
    {
        private readonly StringBuilder _builder = new();
        private readonly int _capBytes;
        private readonly Lock _sync = new();
        private int _byteLength;
        private bool _capped;

        public CappedStringBuilder(int capBytes)
        {
            _capBytes = capBytes;
        }

        public void AppendLine(string? data)
        {
            if (data is null)
            {
                return;
            }

            lock (_sync)
            {
                if (_capped)
                {
                    return;
                }

                // BeginOutputReadLine strips the newline; re-add one (1 UTF-8 byte) so multi-line output is preserved.
                const int newlineBytes = 1;
                var remaining = _capBytes - _byteLength - newlineBytes;
                if (remaining < 0)
                {
                    _capped = true;
                    return;
                }

                var lineBytes = Encoding.UTF8.GetByteCount(data);
                var toAppend = data;
                if (lineBytes > remaining)
                {
                    toAppend = TruncateToUtf8ByteBudget(data, remaining);
                    _capped = true;
                }

                _builder.Append(toAppend).Append('\n');
                _byteLength += Encoding.UTF8.GetByteCount(toAppend) + newlineBytes;
            }
        }

        public bool IsTruncated
        {
            get
            {
                lock (_sync)
                {
                    return _capped;
                }
            }
        }

        public override string ToString()
        {
            lock (_sync)
            {
                return _builder.ToString();
            }
        }

        // Returns the longest prefix of value whose UTF-8 encoding is <= budget bytes, never splitting a rune.
        private static string TruncateToUtf8ByteBudget(string value, int budget)
        {
            if (budget <= 0)
            {
                return string.Empty;
            }

            var used = 0;
            var enumerator = value.EnumerateRunes();
            var lastCharIndex = 0;
            var charIndex = 0;
            foreach (var rune in enumerator)
            {
                var runeBytes = rune.Utf8SequenceLength;
                if (used + runeBytes > budget)
                {
                    break;
                }

                used += runeBytes;
                charIndex += rune.Utf16SequenceLength;
                lastCharIndex = charIndex;
            }

            return value[..lastCharIndex];
        }
    }

    /// <summary>
    ///     A single in-flight command: the spawned process plus the per-command cancel source that best-effort cancel
    ///     and sandbox kill fire to make <see cref="ExecuteAsync" /> return a non-throwing Completed=false result.
    /// </summary>
    private sealed class InFlightExecution
    {
        private readonly CancellationTokenSource _cancelSource;

        public InFlightExecution(Process process, CancellationTokenSource cancelSource)
        {
            Process = process;
            _cancelSource = cancelSource;
        }

        public Process Process { get; }

        public void RequestCancel()
        {
            try
            {
                _cancelSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The command already completed and disposed its source; nothing to cancel.
            }
        }
    }

    private sealed class JailState
    {
        public JailState(SandboxHandle handle, string jailRoot, bool preserveJailRoot = false)
        {
            Handle = handle;
            JailRoot = jailRoot;
            PreserveJailRoot = preserveJailRoot;
        }

        public SandboxHandle Handle { get; }

        public string JailRoot { get; }

        public bool PreserveJailRoot { get; }

        public object Sync { get; } = new();

        public bool Alive { get; set; } = true;

        public ConcurrentDictionary<string, InFlightExecution> InFlight { get; } = new(StringComparer.Ordinal);
    }
}
