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
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>
///     The <c>process</c> sandbox <see cref="ISandboxRuntimeProvider" /> — the <b>process-jail provider</b>: backs
///     AgentHome and Coder with a supervised child <see cref="Process" /> rooted at a node-scoped working-directory
///     jail on the worker host. It owns no Docker, no gRPC, and no HostAgent. It is the drop-in replacement for the
///     DELETED <c>LocalContainerSandboxProvider</c> (the old Docker/gRPC container provider removed by the runtime
///     re-architecture epic), holding the exact same provider-neutral contract so AgentHome's copy → run → export →
///     apply loop is byte-behavior-identical.
///     <para>
///         It is NOT a predecessor of, nor superseded by, the Development Mode container provider added under
///         ADR 0004. Those two are SIBLINGS behind one SPI, chosen per feature (plan decision D2): Development Mode
///         gets the container provider at S3.12, while AgentHome (4 injection sites) and Coder (3 sites) stay here.
///         All soft-guard logic (working-dir jail, path canonicalization, no-follow open, byte budgets, timeout,
///         tree-kill) lives INSIDE this class; a future hardware-isolated (MXC) provider replaces the whole provider,
///         not the contract.
///     </para>
///     <para>
///         Security posture (v1): this is supervised execution, NOT an OS isolation boundary. That has not changed,
///         and this provider gains NO Docker. ADR 0004 permits Docker for Development Mode build/test/lint execution
///         only and says nothing about this provider's posture; under that ADR's decision 4, AgentHome and Coder stay
///         HERE, so what follows is their actual security envelope. MXC remains the long-term hard-isolation backend;
///         the Development Mode container provider is an interim sibling behind the same SPI, and the
///         <see cref="ISandboxRuntimeProvider" /> seam is NOT closed by it.
///     </para>
///     <para>
///         Enforced unconditionally: the working-directory jail, path/symlink guards, a scrubbed child environment (the
///         worker's secret-bearing environment is NOT inherited — only a fixed system/toolchain allow-list is
///         forwarded), the per-command timeout, tree-kill, captured-output byte caps, and a jail-directory disk ceiling
///         on the child's OWN writes.
///     </para>
///     <para>
///         Enforced only where the host supplies the mechanism, measured once by
///         <see cref="ISandboxContainmentProbe" />: CPU/memory/PID ceilings via a transient systemd user scope
///         (cgroup v2), and network egress denial via a fresh empty network namespace. Egress denial is DEFAULT-DENY
///         and has no allowlist — <see cref="SandboxNetworkPolicy.None" /> (the default posture of
///         <see cref="SandboxCreateRequest" />) is served by removing all egress, while a caller that genuinely needs
///         the host network must ask for <see cref="SandboxNetworkPolicy.Unrestricted" /> explicitly.
///         <see cref="SandboxNetworkPolicy.Restricted" /> stays unsupported because an allowlist needs machinery this
///         provider does not have.
///     </para>
///     <para>
///         Read that egress claim precisely: it holds ONLY where the mechanism is actually active. On a host without
///         user namespaces — and on every non-Linux host, where the mechanism is not implemented — the provider
///         degrades, does not advertise <see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" />, and the
///         upstream approval gate remains the interim control. "The sandbox blocks the network" is NOT true as a flat
///         statement about this provider.
///     </para>
///     <para>
///         The honesty invariant runs in BOTH directions and is mechanical, not maintained by hand: advertisement
///         (<see cref="Capabilities" />) and enforcement (the launch path) read the SAME probe, so a capability is
///         advertised if and only if a mechanism is active, and a request for a guarantee this host cannot deliver is
///         rejected fail-closed (<see cref="SandboxCapabilityNotSupportedException" />) rather than silently downgraded.
///         Where a mechanism is absent the provider logs, runs without it, and claims nothing.
///     </para>
///     <para>
///         Still absent by design: read-only mounts, a network allowlist, and any hardware isolation boundary. The
///         single-user local-node threat model accepts these — risky execution is approval-gated upstream — and they
///         are deferred to MXC.
///     </para>
/// </summary>
// Serves BOTH per-feature roles (D2): AgentHome/Coder resolve it through IAgentSandboxRuntimeProvider, and Development
// Mode resolves it through IDevelopmentSandboxRuntimeProvider until an operator selects a container provider. When both
// roles name this provider they resolve the SAME DI singleton — see the _jailRoot comment for why that matters.
public sealed class ProcessSandboxRuntimeProvider : IAgentSandboxRuntimeProvider, IDevelopmentSandboxRuntimeProvider, IDisposable
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
    private readonly ISandboxLauncher _launcher;
    private readonly ILogger<ProcessSandboxRuntimeProvider> _logger;
    private readonly ISandboxMarkerStore _markerStore;

    private readonly long _maxCopyFileBytes;
    private readonly long _maxJailDiskBytes;
    private readonly ConcurrentDictionary<string, JailState> _sandboxes = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    // The logger, launcher and marker store are optional so tests can construct the provider directly;
    // ActivatorUtilities injects them in production. A null launcher/store means "real host behavior", so a directly
    // constructed provider is hardened exactly like the production one rather than silently weaker.
    public ProcessSandboxRuntimeProvider(IOptions<LocalContainerOptions> copyOptions,
        TimeProvider timeProvider,
        ILogger<ProcessSandboxRuntimeProvider>? logger = null,
        ISandboxLauncher? launcher = null,
        ISandboxMarkerStore? markerStore = null)
    {
        ArgumentNullException.ThrowIfNull(copyOptions);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<ProcessSandboxRuntimeProvider>.Instance;
        _launcher = launcher ?? new SandboxLauncher(new HostSandboxContainmentProbe());
        _markerStore = markerStore ?? new FileSandboxMarkerStore();

        // Reuse the existing per-file copy ceiling so the jail's byte-cap-on-re-read matches the container provider's
        // (64 MiB default).
        _maxCopyFileBytes = copyOptions.Value.MaxCopyFileBytes;

        // The child's OWN writes into the jail are bounded separately: MaxCopyFileBytes governs only the host→jail
        // copy-in re-read, so without this a runaway command could fill the host disk from inside the jail.
        _maxJailDiskBytes = copyOptions.Value.MaxJailDiskBytes;

        // A worker-local jail container directory owned by this provider instance. The provider is a DI singleton, so
        // there is exactly one container root per running worker process; the instance suffix keeps two providers (e.g.
        // concurrent tests, or a restart racing teardown) from colliding on each other's node-scoped jails. Each
        // node-scoped sandbox is a subdirectory under it, named deterministically from the attach key.
        _jailRoot = Path.Combine(SandboxPaths.ContainerRoot, Guid.NewGuid().ToString("N"));
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

    public SandboxProviderCapabilities Capabilities
    {
        get
        {
            // Always served, mechanism-independent: copy-into / copy-out (local FS within the jail), per-command
            // cancellation (tree-kill), attach (reattach by key), kill (tree-kill + invalidate), trusted host workspace.
            var capabilities = SandboxProviderCapabilities.SupportsCopyInto
                               | SandboxProviderCapabilities.SupportsCopyOut
                               | SandboxProviderCapabilities.SupportsCommandCancellation
                               | SandboxProviderCapabilities.SupportsAttach
                               | SandboxProviderCapabilities.SupportsKill
                               | SandboxProviderCapabilities.SupportsTrustedHostWorkspace;

            // Never served: read-only mounts (there is no mount layer).
            //
            // Served ONLY where the host supplies the mechanism. Reading the launcher's probe here — the same probe the
            // launch path applies — is what makes the honesty invariant mechanical: these two flags cannot be advertised
            // on a host where the corresponding wrapper would not actually be applied, because there is one source of
            // truth rather than two that must be kept in step by hand.
            var containment = _launcher.Containment;
            if (containment.SupportsResourceLimits)
            {
                capabilities |= SandboxProviderCapabilities.SupportsResourceLimits;
            }

            if (containment.SupportsNetworkIsolation)
            {
                capabilities |= SandboxProviderCapabilities.SupportsNetworkPolicy;
            }

            return capabilities;
        }
    }

    public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail-closed capability contract, resolved against what this HOST can actually do. Anything the provider
        // cannot deliver here is refused up front rather than silently downgraded; anything it can is captured in the
        // launch policy and applied to every command this sandbox runs.
        var launchPolicy = BuildLaunchPolicy(request);

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
            _sandboxes[sandboxId] = new JailState(handle, jailDirectory, launchPolicy, request.TrustedHostWorkspace is not null);
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

        // Wrap the composed command in the strongest containment this host supports (process group, cgroup ceilings,
        // empty network namespace). This rewrites only FileName/ArgumentList and layers in the wrapper's own
        // environment, so the jail working directory, the scrubbed environment allow-list, stream redirection, the
        // timeout and tree-kill all continue to behave exactly as before. It is deliberately applied AFTER the
        // environment scrub, because the resource-limit wrapper needs the user-bus address the scrub would otherwise
        // have removed — and strips it again before the sandboxed executable runs.
        var launch = _launcher.Apply(startInfo, state.LaunchPolicy);

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

        // Record the live process group so a hard host kill (which skips Dispose/KillAsync entirely) leaves something
        // the next start can reap. Only meaningful when the child really is a group leader — see the marker's own docs
        // for why a non-leader pid must never be used as a group id.
        var markerId = WriteProcessMarker(state, process, launch);

        // A per-command source that best-effort cancel (CancelCommandAsync) and sandbox kill (KillAsync) fire. Its
        // firing yields a non-throwing Completed=false result — parity with the fake's CancelCommandAsync — distinct
        // from a caller-token cancel (which throws) and a timeout (which returns a timed-out result).
        var commandCancelSource = new CancellationTokenSource();
        var inFlight = new InFlightExecution(process, commandCancelSource);
        if (!state.InFlight.TryAdd(request.ExecutionId, inFlight))
        {
            // Another command is already in flight under this execution id; kill the just-started one and reject.
            TreeKill(process);
            await KillProcessGroupIfLeaderAsync(launch, process).ConfigureAwait(false);
            if (markerId is not null)
            {
                _markerStore.Delete(markerId);
            }

            process.Dispose();
            commandCancelSource.Dispose();
            throw new InvalidOperationException($"Execution id '{request.ExecutionId}' is already in flight for this sandbox.");
        }

        // A timeout (not a caller cancel) yields a non-throwing TimedOut result; a caller cancel propagates
        // OperationCanceledException; a best-effort command cancel yields a non-throwing Completed=false result.
        using var timeoutSource = new CancellationTokenSource();
        using var diskCapSource = new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, commandCancelSource.Token, diskCapSource.Token);
        if (request.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            timeoutSource.CancelAfter(timeout);
        }

        // Bound the child's OWN writes into the jail. MaxCopyFileBytes governs only the host→jail copy-in re-read, so
        // without this a runaway command could fill the host disk from inside the jail and nothing would stop it.
        using var diskWatchdog = StartJailDiskWatchdog(state, diskCapSource, linkedSource.Token);

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
        catch (OperationCanceledException) when (diskCapSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The child wrote past the jail disk ceiling: tree-kill and return the same non-throwing incomplete shape as
            // a timeout, with an explanatory StandardError so the AgentHome run flow can tell the user WHY it stopped.
            TreeKill(process);
            await KillProcessGroupIfLeaderAsync(launch, process).ConfigureAwait(false);
            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardError = string.Create(CultureInfo.InvariantCulture,
                    $"Command exceeded the sandbox jail disk ceiling of {_maxJailDiskBytes} bytes and was terminated."),
                Completed = false,
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
            await KillProcessGroupIfLeaderAsync(launch, process).ConfigureAwait(false);
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
            await KillProcessGroupIfLeaderAsync(launch, process).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _ = state.InFlight.TryRemove(request.ExecutionId, out _);

            // The command is over one way or another, so its marker has done its job. Deleting it here is what keeps the
            // startup reaper's work proportional to actual orphans rather than to every command ever run.
            if (markerId is not null)
            {
                _markerStore.Delete(markerId);
            }

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

    // ---- containment helpers (launch marker, group kill, jail disk watchdog) ----

    /// <summary>
    ///     Writes the orphan-reaper marker for a just-started child. Returns the marker id, or <see langword="null" />
    ///     when no marker was written.
    ///     <para>
    ///         A marker is written ONLY when the child was launched under <c>setsid</c>, because the reaper signals with
    ///         <c>kill(-pgid)</c> and the recorded pid is only a process-group id in that case. Recording a non-leader
    ///         pid would mean the reaper later signalled whatever group that pid belonged to — in the worst case the
    ///         worker's own — so the absence of the mechanism must mean the absence of a marker, not a guess.
    ///     </para>
    /// </summary>
    private string? WriteProcessMarker(JailState state, Process process, SandboxLaunchDescriptor launch)
    {
        if (!launch.AppliedProcessGroup)
        {
            return null;
        }

        try
        {
            var processId = process.Id;

            // The pid-reuse guard is only as good as the start time recorded alongside it; without one, skip the marker
            // rather than record a group id the reaper could not verify before signalling.
            var startTicks = new LinuxSandboxProcessGroupKiller().GetProcessStartTicks(processId);
            if (startTicks is null)
            {
                return null;
            }

            return _markerStore.Write(new SandboxProcessMarker
            {
                SandboxId = state.Handle.SandboxId,
                ProcessGroupId = processId,
                LeaderStartTicks = startTicks.Value,
                JailPath = state.JailRoot,
                PreserveJail = state.PreserveJailRoot,
                OwnerProcessId = Environment.ProcessId,
                CreatedAt = _timeProvider.GetUtcNow()
            });
        }
        catch (InvalidOperationException)
        {
            // The process exited before its pid could be read — nothing to reap, so nothing to record.
            return null;
        }
    }

    /// <summary>
    ///     Signals the child's whole process group after a tree-kill, as defense in depth. <see cref="TreeKill" />
    ///     remains the primary mechanism and is unchanged; this catches a descendant that detached from the process tree
    ///     the runtime walks but is still in the group the child leads. A no-op unless the child really is a group
    ///     leader.
    /// </summary>
    private async Task KillProcessGroupIfLeaderAsync(SandboxLaunchDescriptor launch, Process process)
    {
        if (!launch.AppliedProcessGroup || !OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            await new LinuxSandboxProcessGroupKiller(_timeProvider).KillProcessGroupAsync(process.Id).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No associated process any more — the tree-kill already finished the job.
        }
        catch (Exception exception)
        {
            // Best-effort teardown never throws into the run flow, matching TerminateState/Dispose.
            _logger.LogDebug(exception, "Best-effort sandbox process-group kill failed.");
        }
    }

    /// <summary>
    ///     Starts the jail disk watchdog for one command, or returns a no-op when it does not apply. Cancelling
    ///     <paramref name="diskCapSource" /> is what unblocks the command's wait and routes it to the over-cap result.
    ///     <para>
    ///         The ceiling is measured as GROWTH from a baseline taken now, not as absolute jail size — the control
    ///         exists to bound the child's OWN writes, and a jail can legitimately start non-empty after copy-in.
    ///     </para>
    ///     <para>
    ///         It is skipped for an engine-managed trusted host workspace: that directory is the user's own checkout,
    ///         its existing size is not ours to police, and walking a large repository every tick would cost more than
    ///         the control is worth there.
    ///     </para>
    /// </summary>
    private IDisposable StartJailDiskWatchdog(JailState state, CancellationTokenSource diskCapSource, CancellationToken commandToken)
    {
        if (_maxJailDiskBytes <= 0 || state.PreserveJailRoot)
        {
            return new NoOpDisposable();
        }

        var watchdogSource = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
        var jailRoot = state.JailRoot;
        var ceiling = _maxJailDiskBytes;

        _ = Task.Run(async () =>
        {
            try
            {
                // A coarse interval: this is a safety net against a runaway writer, not a byte-accurate meter, and a
                // tight loop would cost more than the protection is worth.
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                var baseline = MeasureDirectoryBytes(jailRoot, long.MaxValue);

                while (await timer.WaitForNextTickAsync(watchdogSource.Token).ConfigureAwait(false))
                {
                    // Summing stops as soon as the ceiling is passed, so the cost stays bounded even when a command is
                    // actively filling the jail.
                    var current = MeasureDirectoryBytes(jailRoot, baseline + ceiling);
                    if (current - baseline > ceiling)
                    {
                        _logger.LogWarning(
                            "Sandbox command exceeded the jail disk ceiling of {Ceiling} bytes (grew {Grown} bytes); terminating it.",
                            ceiling,
                            current - baseline);
                        await diskCapSource.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The command finished first — the normal path.
            }
            catch (ObjectDisposedException)
            {
                // The command completed and disposed its sources while this tick was in flight.
            }
        }, CancellationToken.None);

        return watchdogSource;
    }

    /// <summary>
    ///     Sums the byte length of every file under <paramref name="root" />, stopping early once
    ///     <paramref name="ceiling" /> is exceeded. Entirely best-effort: files and directories can vanish mid-walk
    ///     while a command is running, and a partial measurement is the right answer for a safety net.
    /// </summary>
    private static long MeasureDirectoryBytes(string root, long ceiling)
    {
        long total = 0;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // Never follow a symlink out of the jail while measuring: a planted link could otherwise make the
                // watchdog walk (and bill the command for) an arbitrary host tree.
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                try
                {
                    total += file.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The file vanished between enumeration and stat; skip it.
                    continue;
                }

                if (total > ceiling)
                {
                    return total;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // An unreadable or deleted jail simply yields what was counted so far.
        }

        return total;
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
            // Nothing to release: the watchdog did not start.
        }
    }

    // ---- jail / state helpers ----

    /// <summary>
    ///     The enforcement half of the capability-honesty invariant: a guarantee this host cannot deliver is refused up
    ///     front, so a caller can never believe it received isolation the provider did not apply. It reads the same
    ///     containment probe as <see cref="Capabilities" />, so what is rejected here is exactly what is not advertised
    ///     there.
    /// </summary>
    private SandboxLaunchPolicy BuildLaunchPolicy(SandboxCreateRequest request)
    {
        var containment = _launcher.Containment;

        // Restricted means an egress allow-list, which needs a veth pair plus a per-namespace ruleset. That is
        // explicitly out of scope (default-deny only), so it is never honored regardless of host capability.
        if (request.NetworkPolicy == SandboxNetworkPolicy.Restricted)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{Name}' sandbox provider has no network allow-list mechanism and cannot honor NetworkPolicy.Restricted. Use NetworkPolicy.None for default-deny egress, or an OS-isolated provider for an allow-list."));
        }

        // None means no egress. Honored when the host can create an empty network namespace; rejected fail-closed when
        // it cannot, rather than handing back a sandbox that silently shares the host network.
        var denyEgress = request.NetworkPolicy == SandboxNetworkPolicy.None;
        if (denyEgress && !containment.SupportsNetworkIsolation)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{Name}' sandbox provider cannot deny network egress on this host ({containment.NetworkIsolationUnavailableReason ?? "no mechanism is available"}), so NetworkPolicy.None cannot be honored. Use NetworkPolicy.Unrestricted to accept a shared host network, or an OS-isolated provider."));
        }

        // Resource limits are honored when a transient systemd user scope can impose them, and rejected fail-closed
        // when it cannot — running without the ceiling the caller asked for is exactly the silent downgrade this
        // contract exists to prevent.
        var limits = request.ResourceLimits;
        var wantsLimits = limits is not null && (limits.CpuCount.HasValue || limits.MemoryMb.HasValue || limits.PidsLimit.HasValue);
        if (wantsLimits && !containment.SupportsResourceLimits)
        {
            throw new SandboxCapabilityNotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The '{Name}' sandbox provider cannot enforce resource limits (CPU/memory/PID) on this host ({containment.ResourceLimitsUnavailableReason ?? "no mechanism is available"}). Remove SandboxResourceLimits or use a provider that advertises SupportsResourceLimits."));
        }

        return new SandboxLaunchPolicy
        {
            ResourceLimits = wantsLimits ? limits : null,
            DenyNetworkEgress = denyEgress
        };
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
        public JailState(SandboxHandle handle, string jailRoot, SandboxLaunchPolicy launchPolicy, bool preserveJailRoot = false)
        {
            Handle = handle;
            JailRoot = jailRoot;
            LaunchPolicy = launchPolicy;
            PreserveJailRoot = preserveJailRoot;
        }

        public SandboxHandle Handle { get; }

        public string JailRoot { get; }

        /// <summary>The containment resolved at create time and applied to every command this sandbox runs.</summary>
        public SandboxLaunchPolicy LaunchPolicy { get; }

        public bool PreserveJailRoot { get; }

        public object Sync { get; } = new();

        public bool Alive { get; set; } = true;

        public ConcurrentDictionary<string, InFlightExecution> InFlight { get; } = new(StringComparer.Ordinal);
    }
}
