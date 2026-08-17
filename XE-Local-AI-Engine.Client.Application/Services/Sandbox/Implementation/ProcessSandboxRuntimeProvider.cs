namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
///         ADR 0004. Those two are SIBLINGS behind one SPI, chosen per feature: Development Mode
///         gets the container provider, while AgentHome (4 injection sites) and Coder (3 sites) stay here.
///         All soft-guard logic (working-dir jail, path canonicalization, no-follow open, byte budgets, timeout,
///         tree-kill) is owned by this provider — the path/symlink/no-follow half factored out into
///         <see cref="SandboxJailPathGuard" /> so the guard pair is one audit target, and the question of which jails
///         exist (create/attach/evict/terminate) into <see cref="SandboxLifecycleRegistry" />, which owns the live
///         sandbox set this class executes commands inside; a future hardware-isolated (MXC) provider replaces the
///         whole provider, not the contract.
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
// Serves BOTH per-feature roles: AgentHome/Coder resolve it through IAgentSandboxRuntimeProvider, and Development
// Mode resolves it through IDevelopmentSandboxRuntimeProvider until an operator selects a container provider. When both
// roles name this provider they resolve the SAME DI singleton — see the _jailRoot comment for why that matters.
public sealed class ProcessSandboxRuntimeProvider : IAgentSandboxRuntimeProvider, IDevelopmentSandboxRuntimeProvider, IDisposable
{
    /// <summary>The provider name this registers under for configuration-bound selection.</summary>
    public const string Name = "process";

    // Default captured-output ceiling per stream (stdout / stderr). Mirrors the container provider's bounded transfer
    // posture: capture is capped, and reading stops once the cap is reached so a runaway command cannot exhaust memory.
    private const int DefaultMaxCapturedOutputBytes = 4 * 1024 * 1024;

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
        "LOCALAPPDATA",
        // Windows MACHINE-WIDE configuration roots. These are not decoration: NuGet.Common resolves the machine-wide
        // NuGet configuration directory by reading these names directly and Path.Combine-ing the result, so with all
        // of them absent the combine receives null and `dotnet restore` dies on
        // "NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')" before it looks at a single
        // package. The .NET SDK reads the same roots to locate installed workload records, which is the
        // "An issue was encountered verifying workloads." that accompanies it. Measured against the 10.0.302 SDK:
        // NuGet.Common.dll carries exactly PROGRAMDATA, PROGRAMFILES, PROGRAMFILES(X86) and ALLUSERSPROFILE.
        // They name shared installation roots, not user data, so forwarding them leaks nothing the child could not
        // already read from disk.
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ALLUSERSPROFILE"
    ];

    private readonly string _jailRoot;
    private readonly ISandboxLauncher _launcher;
    private readonly ILogger<ProcessSandboxRuntimeProvider> _logger;
    private readonly ISandboxMarkerStore _markerStore;

    private readonly long _maxCopyFileBytes;
    private readonly long _maxJailDiskBytes;
    private readonly SandboxLifecycleRegistry _registry;
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

        // The registry owns the live sandbox set rooted at that container directory; this provider reaches every jail
        // through it rather than keeping a second dictionary.
        _registry = new SandboxLifecycleRegistry(_jailRoot, _launcher, _timeProvider);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        // Dispose = ensure tree-kill of any live process the provider still supervises. Best-effort: a sandbox can
        // already be torn down.
        _registry.TerminateAll();

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
        return _registry.CreateOrAttachAsync(request, cancellationToken);
    }

    public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
    {
        return _registry.ConnectAsync(attachKey, cancellationToken);
    }

    public async Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _registry.GetAliveState(handle);
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
            SandboxProcessTree.TreeKill(process);
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
            SandboxProcessTree.TreeKill(process);
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
            SandboxProcessTree.TreeKill(process);
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
            SandboxProcessTree.TreeKill(process);
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
            SandboxProcessTree.TreeKill(process);
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

        var state = _registry.GetAliveState(handle);
        await SandboxFileSurveyOperations.CopyIntoAsync(state.JailRoot, request, _maxCopyFileBytes, cancellationToken).ConfigureAwait(false);
    }

    public Task ResetDirectoryAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _registry.GetAliveState(handle);
        SandboxFileSurveyOperations.ResetDirectory(state.JailRoot, sandboxPath);

        return Task.CompletedTask;
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

        var state = _registry.GetAliveState(handle);
        return await SandboxFileSurveyOperations.ReadFileAsync(state.JailRoot, sandboxPath, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ISandboxRuntimeProvider.ListFilesAsync" />
    public Task<IReadOnlyList<string>> ListFilesAsync(SandboxHandle handle,
        SandboxListFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = ResolveSurveyState(handle, request.DirectoryPath, cancellationToken);

        return Task.FromResult(SandboxFileSurveyOperations.ListFiles(state.JailRoot, request, cancellationToken));
    }

    /// <inheritdoc cref="ISandboxRuntimeProvider.SearchTextAsync" />
    public Task<IReadOnlyList<string>> SearchTextAsync(SandboxHandle handle,
        SandboxSearchTextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = ResolveSurveyState(handle, request.DirectoryPath, cancellationToken);

        return Task.FromResult(SandboxFileSurveyOperations.SearchText(state.JailRoot, request, cancellationToken));
    }

    /// <summary>
    ///     The handle-side half of a survey's entry checks — argument validation and the live-sandbox lookup — kept
    ///     together so both surveys enter <see cref="SandboxFileSurveyOperations" /> under identical conditions. The
    ///     jail-side half (path resolution + symlink walk) lives with the survey itself.
    /// </summary>
    private JailState ResolveSurveyState(SandboxHandle handle, string directoryPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        return _registry.GetAliveState(handle);
    }

    public async Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _registry.GetAliveState(handle);
        await SandboxFileSurveyOperations.CopyOutAsync(state.JailRoot, request, cancellationToken).ConfigureAwait(false);
    }

    public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        cancellationToken.ThrowIfCancellationRequested();

        // Best-effort: cancel + tree-kill the in-flight command by execution id. Firing the command-cancel source makes
        // the in-flight ExecuteAsync return a non-throwing Completed=false result. A missing id or already-gone sandbox
        // is a no-op (parity with the container/fake providers).
        if (_registry.FindState(handle.SandboxId) is { } state
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

        _registry.RemoveAndTerminate(handle.SandboxId);

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
    ///     Signals the child's whole process group after a tree-kill, as defense in depth.
    ///     <see cref="SandboxProcessTree.TreeKill" /> remains the primary mechanism and is unchanged; this catches a
    ///     descendant that detached from the process tree the runtime walks but is still in the group the child leads.
    ///     A no-op unless the child really is a group leader.
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
                        _logger.LogWarning("Sandbox command exceeded the jail disk ceiling of {Ceiling} bytes (grew {Grown} bytes); terminating it.",
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

    // ---- jail working-directory helper ----

    private static string ResolveWorkingDirectory(JailState state, string? requestedWorkingDirectory)
    {
        if (requestedWorkingDirectory is null)
        {
            return state.JailRoot;
        }

        var canonicalPath = SandboxJailPathGuard.ResolveJailPath(state.JailRoot, requestedWorkingDirectory);
        SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(state.JailRoot, canonicalPath, requestedWorkingDirectory);
        return canonicalPath;
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

}
