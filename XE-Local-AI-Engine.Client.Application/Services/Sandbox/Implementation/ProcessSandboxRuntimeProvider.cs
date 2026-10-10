namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>
///     The <c>process</c> <see cref="ISandboxRuntimeProvider" />: a supervised child <see cref="Process" /> in a
///     node-scoped working-directory jail, running the host's own toolchain over the provider-neutral
///     copy → run → export → apply lifecycle with no container dependency.
/// </summary>
/// <remarks>
///     Supervised execution; an OS boundary only where a measured mechanism adds one (bubblewrap; on Windows the Preview MXC
///     AppContainer boundary, ADR 0019). Always enforced: the jail, <see cref="SandboxJailPathGuard" />'s guards, a scrubbed environment,
///     the timeout, tree-kill, output caps and a jail-disk ceiling. Everything else holds only where the probe measured it, which
///     <see cref="Capabilities" /> reads too, so a request is refused, not downgraded. Security wiki §7; ADR 0004 and ADR 0007.
/// </remarks>
/// <seealso href="../../../../docs/wiki/12-security-and-privacy.md" />
// Serves BOTH per-feature roles — AgentHome/Coder through IAgentSandboxRuntimeProvider, Development Mode through
// IDevelopmentSandboxRuntimeProvider — and both resolve the SAME DI singleton; see the _jailRoot comment for why.
public sealed class ProcessSandboxRuntimeProvider : IAgentSandboxRuntimeProvider, IDevelopmentSandboxRuntimeProvider, IWorkSessionSandboxRuntimeProvider, IDisposable
{
    /// <summary>The provider name this registers under for configuration-bound selection.</summary>
    public const string Name = "process";

    // Default captured-output ceiling per stream (stdout / stderr). Mirrors the container provider's bounded transfer
    // posture: capture is capped, and reading stops once the cap is reached so a runaway command cannot exhaust memory.
    private const int DefaultMaxCapturedOutputBytes = 4 * 1024 * 1024;

    private const string SandboxKilledBeforeStart = "The sandbox was killed before the command started.";

    /// <summary>
    ///     The only environment variables a sandboxed child inherits from the worker: system and toolchain names the
    ///     fixed production executables need on Linux and Windows, none of them secret-bearing.
    /// </summary>
    /// <remarks>
    ///     SECURITY INVARIANT: a sandboxed child never inherits the worker environment, which holds cloud API keys,
    ///     OAuth tokens, the node SQLite key and connection strings. The child starts EMPTY, is repopulated only from
    ///     this list, and the caller's explicit <c>request.Environment</c> is layered on top. Never widen it to inherit
    ///     the parent. Names absent on the current OS are skipped, and lookup is OS-correct (case-insensitive on
    ///     Windows).
    /// </remarks>
    internal static readonly string[] InheritableEnvironmentAllowlist =
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
        // Windows MACHINE-WIDE roots: NuGet.Common and the SDK read exactly these four to locate machine-wide NuGet config
        // and workload records, combining unguarded, so absent `dotnet restore` dies on a null path. Shared, not user data.
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ALLUSERSPROFILE"
    ];

    private readonly string _jailRoot;
    private readonly ISandboxLauncher _launcher;
    private readonly ILogger<ProcessSandboxRuntimeProvider> _logger;
    private readonly ISandboxMarkerStore _markerStore;
    private readonly IMxcSandboxRuntime? _mxcRuntime;
    private readonly IExecutionPreviewPolicy? _previewPolicy;

    private readonly long _maxCopyFileBytes;
    private readonly long _maxJailDiskBytes;
    private readonly SandboxLifecycleRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    // Optional so tests can construct the provider directly; DI injects them. A null one means hardened host behaviour: real
    // launcher and marker store, and previews OFF.
    public ProcessSandboxRuntimeProvider(IOptions<LocalContainerOptions> copyOptions,
        TimeProvider timeProvider,
        ILogger<ProcessSandboxRuntimeProvider>? logger = null,
        ISandboxLauncher? launcher = null,
        ISandboxMarkerStore? markerStore = null,
        IExecutionPreviewPolicy? previewPolicy = null,
        IMxcSandboxRuntime? mxcRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(copyOptions);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<ProcessSandboxRuntimeProvider>.Instance;
        _launcher = launcher ?? new SandboxLauncher(new HostSandboxContainmentProbe());
        _markerStore = markerStore ?? new FileSandboxMarkerStore();
        _previewPolicy = previewPolicy;
        // The MXC SDK is reached only for a launch the AppContainer boundary serves, which only a Windows probe can produce; elsewhere
        // there is no runtime and such a descriptor fails closed. Injectable so the MXC path is unit-tested on Linux through a fake.
        _mxcRuntime = mxcRuntime ?? (OperatingSystem.IsWindows() ? new MxcSandboxRuntime() : null);

        // Reuse the existing per-file copy ceiling so the jail's byte-cap-on-re-read matches the container provider's
        // (64 MiB default).
        _maxCopyFileBytes = copyOptions.Value.MaxCopyFileBytes;

        // The child's OWN writes into the jail are bounded separately; MaxCopyFileBytes governs only the host→jail copy-in
        // re-read. This is the NODE-WIDE operator ceiling, which a create request may tighten for its own sandbox, never raise.
        _maxJailDiskBytes = copyOptions.Value.MaxJailDiskBytes;

        // A worker-local jail container directory owned by this provider instance, a DI singleton, so there is one root per
        // worker; the suffix stops two providers colliding. Each sandbox is a subdirectory named from the attach key.
        _jailRoot = Path.Combine(SandboxPaths.ContainerRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jailRoot);

        // The registry owns the live sandbox set rooted at that container directory; this provider reaches every jail
        // through it rather than keeping a second dictionary.
        _registry = new SandboxLifecycleRegistry(_jailRoot, _launcher, _timeProvider, _previewPolicy);
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
                               | SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                               // Mechanism-independent: a supervised child running the host's own toolchain. It supplies no
                               // image, which keeps a workload that needs one off this provider.
                               | SandboxProviderCapabilities.SuppliesHostToolchain;

            // Read-only mounts are never served: there is no mount layer. The two flags below are read from the launcher's own
            // probe, the same one the launch path applies, so neither can be advertised where the wrapper would not be.
            var containment = _launcher.Containment;
            if (containment.SupportsResourceLimits)
            {
                capabilities |= SandboxProviderCapabilities.SupportsResourceLimits;
            }

            if (containment.SupportsNetworkIsolation)
            {
                capabilities |= SandboxProviderCapabilities.SupportsNetworkPolicy;
            }

            if (containment.SupportsFilesystemIsolation)
            {
                // Both flags from one probe: here the bubblewrap chain IS both the SandboxIsolationMode.Filesystem contract and
                // the host-filesystem boundary; they are separate only because a container has the second alone.
                capabilities |= SandboxProviderCapabilities.SupportsFilesystemIsolation
                                | SandboxProviderCapabilities.SupportsHostFilesystemBoundary;
            }

            // The AppContainer boundary serves the same floor only while ELIGIBLE (ADR 0019), read per call as the registry does,
            // so turning previews off withdraws the advertisement at once and nothing advertised here is refused at create.
            if (containment.EligibleAppContainerBoundary(_previewPolicy?.PreviewMechanismsEnabled == true) is not null)
            {
                capabilities |= SandboxProviderCapabilities.SupportsAppContainerBoundary
                                | SandboxProviderCapabilities.SupportsFilesystemIsolation
                                | SandboxProviderCapabilities.SupportsHostFilesystemBoundary;
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

        var startInfo = BuildScrubbedStartInfo(state, request, redirectStandardInput: request.StandardInput is not null);

        // Wrap the command in this host's strongest containment; only FileName/ArgumentList change, so the jail, scrub,
        // redirection, timeout and tree-kill hold. After the scrub: the wrapper needs the user-bus address it removes, then strips.
        SandboxLaunchDescriptor launch;
        try
        {
            launch = _launcher.Apply(startInfo,
                state.LaunchPolicy,
                new SandboxLaunchContext
                {
                    JailRoot = state.JailRoot,
                    CommandTimeout = request.Timeout,
                    CommandEnvironment = request.Environment
                });
        }
        catch (SandboxIsolationUnavailableException exception)
        {
            // FAIL CLOSED, non-throwing. The host was measured able to isolate, so something changed underneath; running anyway
            // would put a workload promised a boundary onto the host filesystem. The caller learns why in a failed-launch shape.
            _logger.LogError(exception, "The sandbox filesystem boundary could not be established; the command was not run.");

            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardError = $"The sandbox filesystem boundary could not be established ({exception.Message}); the command was not run.",
                Completed = false,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }

        // Anchor the occupancy baseline BEFORE the child starts, while the jail holds only what the ENGINE staged: measured
        // after, a command's own first write is free for it and every later command here. Null means the watchdog does not apply.
        var jailDiskCeiling = ResolveJailDiskCeiling(state);
        var jailOccupancyBaseline = CaptureJailOccupancyBaseline(state, jailDiskCeiling);

        // Claim the scope BEFORE systemd-run can create it, which it does as its first act: a marker written after the launch
        // leaves a window in which another worker's startup sweep finds the scope unclaimed and SIGKILLs a command that started.
        var pendingMarkerId = launch.ScopeUnitName is null ? null : PreRegisterProcessMarker(state, launch);

        // Capture stdout/stderr via the line pump with a hard per-stream byte budget: appending stops past the cap so a
        // runaway command cannot exhaust memory, while the pump keeps draining so the child never blocks on a full buffer.
        var standardOutputBuilder = new CappedStringBuilder(DefaultMaxCapturedOutputBytes);
        var standardErrorBuilder = new CappedStringBuilder(DefaultMaxCapturedOutputBytes);

        ISandboxChildProcess process;
        try
        {
            process = await StartChildAsync(startInfo, launch, standardOutputBuilder.AppendLine, standardErrorBuilder.AppendLine, cancellationToken);
        }
        catch (Exception exception) when (IsLaunchFailure(exception, launch, cancellationToken))
        {
            launch.LaunchResources?.Dispose();
            if (pendingMarkerId is not null)
            {
                // Nothing was ever launched, so nothing claims that unit name any more.
                _markerStore.Delete(pendingMarkerId);
            }

            // The executable could not be launched (not found / not executable, or MXC refused the spawn). Surface a non-completed result
            // rather than throwing, so the AgentHome run flow records a failed command the same way a non-zero exit does.
            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardError = DescribeLaunchFailure(exception, launch),
                Completed = false,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }

        // The chain has been exec'd and the child holds its own copies of every descriptor the argument vector names, so the
        // engine's copies are released here: holding them would leak one descriptor per bind per command.
        launch.LaunchResources?.Dispose();

        // Record the live process group so a hard host kill, which skips Dispose/KillAsync, leaves something the next start can
        // reap. Only meaningful for a real group leader; the marker's docs say why a non-leader pid must never be a group id.
        var markerId = CompleteProcessMarker(state, process, launch, pendingMarkerId);

        // A per-command source that best-effort cancel (CancelCommandAsync) and sandbox kill (KillAsync) fire, yielding a
        // non-throwing Completed=false result, distinct from a caller-token cancel (throws) and a timeout (timed-out result).
        var commandCancelSource = new CancellationTokenSource();
        var inFlight = new InFlightExecution(process, commandCancelSource, launch.ScopeUnitName);
        var registration = TryRegisterInFlight(state, request.ExecutionId, inFlight);
        if (registration != InFlightRegistration.Registered)
        {
            // The sandbox was killed while the child started, or another command is in flight under this execution id: kill the
            // just-started one, which no teardown can reach, and reject.
            process.Kill();
            await TerminateLaunchAsync(launch, process);
            if (markerId is not null)
            {
                _markerStore.Delete(markerId);
            }

            process.Dispose();
            commandCancelSource.Dispose();
            if (registration == InFlightRegistration.SandboxDead)
            {
                return new SandboxCommandResult
                {
                    ExecutionId = request.ExecutionId,
                    ExitCode = -1,
                    StandardError = SandboxKilledBeforeStart,
                    Completed = false,
                    Duration = _timeProvider.GetUtcNow() - startedAt
                };
            }

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

        // Bound the child's OWN writes into the jail; MaxCopyFileBytes governs only the copy-in re-read, so without this a
        // runaway command could fill the host disk. Ceiling and baseline were resolved before launch; only ticking starts here.
        using var diskWatchdog = StartJailDiskWatchdog(state, jailDiskCeiling, jailOccupancyBaseline, diskCapSource, linkedSource.Token);

        try
        {
            if (request.StandardInput is not null)
            {
                await process.WriteStandardInputAndCloseAsync(request.StandardInput, linkedSource.Token);
            }

            // WaitForExitAsync also waits for the output pump to drain, so the captured builders are complete
            // once it returns. The linked token unblocks the wait on a cancel/timeout.
            var exitCode = await process.WaitForExitAsync(linkedSource.Token);

            if (process.TimedOut)
            {
                // MXC enforced the same timeout inside its own wait and reaped the tree: the same shape as the engine's timeout below.
                return new SandboxCommandResult
                {
                    ExecutionId = request.ExecutionId,
                    ExitCode = -1,
                    StandardOutput = standardOutputBuilder.ToString(),
                    StandardError = "Command timed out.",
                    Completed = false,
                    Duration = _timeProvider.GetUtcNow() - startedAt
                };
            }

            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = exitCode,
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
            process.Kill();
            await TerminateLaunchAsync(launch, process);
            return new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardError = string.Create(CultureInfo.InvariantCulture,
                    $"Command exceeded the sandbox jail disk ceiling of {jailDiskCeiling} bytes and was terminated."),
                Completed = false,
                Duration = _timeProvider.GetUtcNow() - startedAt
            };
        }
        catch (OperationCanceledException) when (commandCancelSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A best-effort cancel or sandbox kill fired: tree-kill and return a non-throwing Completed=false result, so AgentHome
            // reads a cancelled command, not a caller cancel. Scope and group kills run here: a tree walk stops at the helper.
            process.Kill();
            await TerminateLaunchAsync(launch, process);
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
            process.Kill();
            await TerminateLaunchAsync(launch, process);
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
            process.Kill();
            await TerminateLaunchAsync(launch, process);
            throw;
        }
        finally
        {
            // Teardown on EVERY path, success included: a command's exit says nothing about its DESCENDANTS, and a reparented orphan
            // outlives the RUN in a reused sandbox, unseen by the sweep once its marker goes. Ordered BEFORE that delete; all idempotent.
            process.Kill();
            await TerminateLaunchAsync(launch, process);

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

    /// <summary>
    ///     Composes the child's <see cref="ProcessStartInfo" />: the jail working directory, the argument vector, and
    ///     the SCRUBBED environment.
    /// </summary>
    /// <remarks>
    ///     SECURITY INVARIANT, and the reason this is one function rather than two copies:
    ///     <see cref="ProcessStartInfo" /> pre-seeds <see cref="ProcessStartInfo.Environment" /> with the FULL parent
    ///     (worker) environment, which holds cloud API keys, OAuth tokens and the node SQLite key. It is cleared and
    ///     repopulated from <see cref="InheritableEnvironmentAllowlist" />, then the caller's explicit request
    ///     environment is layered on top. Both launch paths go through here so neither can drift out of the scrub.
    /// </remarks>
    private static ProcessStartInfo BuildScrubbedStartInfo(JailState state, SandboxCommandRequest request, bool redirectStandardInput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = ResolveWorkingDirectory(state, request.WorkingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            UseShellExecute = false
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var name in InheritableEnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        // The caller is trusted node code composing a fixed command, not the sandboxed child, so its explicit
        // variables may override or add to the allow-list.
        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    /// <inheritdoc cref="ISandboxRuntimeProvider.StartInteractiveAsync" />
    public async Task<ISandboxInteractiveProcess> StartInteractiveAsync(SandboxHandle handle,
        SandboxCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _registry.GetAliveState(handle);
        var startInfo = BuildScrubbedStartInfo(state, request, redirectStandardInput: true);

        SandboxLaunchDescriptor launch;
        try
        {
            launch = _launcher.Apply(startInfo,
                state.LaunchPolicy,
                new SandboxLaunchContext
                {
                    JailRoot = state.JailRoot,
                    // No CommandTimeout: a protocol peer has no per-call deadline, so the scope takes the launcher's default
                    // ceiling, enforced by the USER MANAGER, which is what bounds this jail if the engine is hard-killed.
                    CommandEnvironment = request.Environment
                });
        }
        catch (SandboxIsolationUnavailableException exception)
        {
            // FAIL CLOSED, and here it THROWS rather than returning a failed-command shape: there is no result object to carry a
            // reason, and the caller (a Sandboxed stdio MCP server) must see a refusal, never a host-filesystem peer.
            throw new SandboxCapabilityNotSupportedException($"The sandbox filesystem boundary could not be established ({exception.Message}); the command was not started.",
                exception);
        }

        // Claim the scope BEFORE systemd-run can create it, exactly as the command path does: a marker written after
        // the launch leaves a window in which a second worker's startup sweep finds an unclaimed scope and kills it.
        var pendingMarkerId = launch.ScopeUnitName is null ? null : PreRegisterProcessMarker(state, launch);

        // stderr is always drained, since a child blocks on a full pipe and a stdio MCP server that logs there would deadlock
        // mid-protocol, and only its bounded tail is kept: it is the one place a server that dies on startup says why. stdout stays raw.
        var stderrTail = SandboxStderrTail.For(request);
        ISandboxChildProcess process;
        try
        {
            process = await StartChildAsync(startInfo, launch, onStandardOutputLine: null, stderrTail.Append, cancellationToken);
        }
        catch (Exception exception) when (IsLaunchFailure(exception, launch, cancellationToken))
        {
            launch.LaunchResources?.Dispose();
            if (pendingMarkerId is not null)
            {
                _markerStore.Delete(pendingMarkerId);
            }

            throw new SandboxCapabilityNotSupportedException(DescribeLaunchFailure(exception, launch), exception);
        }

        // The chain has been exec'd and the child holds its own copies of every descriptor the argument vector names, so the
        // engine's copies are released here: one leaked descriptor per bind per server would exhaust the table.
        launch.LaunchResources?.Dispose();

        var markerId = CompleteProcessMarker(state, process, launch, pendingMarkerId);

        // Registered in the jail's in-flight set under the execution id, which is what makes KillAsync, and so the whole teardown
        // (scope cgroup, process group, tree, jail directory), cover this process with no second path.
        var commandCancelSource = new CancellationTokenSource();
        var inFlight = new InFlightExecution(process, commandCancelSource, launch.ScopeUnitName);
        var registration = TryRegisterInFlight(state, request.ExecutionId, inFlight);
        if (registration != InFlightRegistration.Registered)
        {
            process.Kill();
            await TerminateLaunchAsync(launch, process);
            if (markerId is not null)
            {
                _markerStore.Delete(markerId);
            }

            process.Dispose();
            commandCancelSource.Dispose();
            throw registration == InFlightRegistration.SandboxDead
                ? new SandboxHandleInvalidException(SandboxKilledBeforeStart)
                : new InvalidOperationException($"Execution id '{request.ExecutionId}' is already in flight for this sandbox.");
        }

        _logger.LogInformation("Sandboxed interactive command {ExecutionId} started (mechanism {Mechanism}, filesystem isolation {Isolated}).",
            request.ExecutionId,
            launch.MxcRequest is not null ? "MXC AppContainer" : "process",
            launch.AppliedFilesystemIsolation);
        return new InteractiveProcess(this, state, request.ExecutionId, process, launch, markerId, commandCancelSource, stderrTail);
    }

    public async Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _registry.GetAliveState(handle);
        await SandboxFileSurveyOperations.CopyIntoAsync(state.JailRoot, request, _maxCopyFileBytes, cancellationToken);
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
        return await ReadFileAsync(handle, sandboxPath, int.MaxValue, cancellationToken);
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
        return await SandboxFileSurveyOperations.ReadFileAsync(state.JailRoot, sandboxPath, maxBytes, cancellationToken);
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
    ///     together so both surveys enter <see cref="SandboxFileSurveyOperations" /> under identical conditions.
    /// </summary>
    /// <remarks>The jail-side half (path resolution and the symlink walk) lives with the survey itself.</remarks>
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
        await SandboxFileSurveyOperations.CopyOutAsync(state.JailRoot, request, cancellationToken);
    }

    public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        cancellationToken.ThrowIfCancellationRequested();

        // Best-effort: cancel and tree-kill the in-flight command by execution id; firing the cancel source makes ExecuteAsync
        // return a non-throwing Completed=false result. A missing id or gone sandbox is a no-op, as elsewhere.
        if (_registry.FindState(handle.SandboxId) is { } state
            && state.InFlight.TryGetValue(executionId, out var inFlight))
        {
            inFlight.RequestCancel();
        }

        return Task.CompletedTask;
    }

    /// <summary>Registers a just-started child under the jail's lock: it joins the in-flight set of a live jail, or sees the jail dead.</summary>
    /// <remarks>
    ///     A kill that ran while the child was starting (an MXC spawn is awaited) already swept the in-flight set, so registering after it
    ///     would leave the child where no teardown looks.
    /// </remarks>
    private static InFlightRegistration TryRegisterInFlight(JailState state, string executionId, InFlightExecution inFlight)
    {
        lock (state.Sync)
        {
            if (!state.Alive)
            {
                return InFlightRegistration.SandboxDead;
            }

            return state.InFlight.TryAdd(executionId, inFlight) ? InFlightRegistration.Registered : InFlightRegistration.DuplicateExecutionId;
        }
    }

    public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();

        _registry.RemoveAndTerminate(handle.SandboxId);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Starts the child the descriptor names: an MXC ProcessContainer spawn when the AppContainer boundary serves the launch, otherwise
    ///     the (possibly wrapped) start info exactly as before.
    /// </summary>
    /// <remarks>A null <paramref name="onStandardOutputLine" /> leaves stdout raw for an interactive peer; stderr is always pumped.</remarks>
    private async Task<ISandboxChildProcess> StartChildAsync(ProcessStartInfo startInfo,
        SandboxLaunchDescriptor launch,
        Action<string?>? onStandardOutputLine,
        Action<string?> onStandardErrorLine,
        CancellationToken cancellationToken)
    {
        if (launch.MxcRequest is { } mxcRequest)
        {
            if (_mxcRuntime is null)
            {
                throw new SandboxIsolationUnavailableException("the MXC runtime is not available in this process");
            }

            // Journal the grants BEFORE MXC writes them, so a crash before ClearPolicyOnExit runs leaves the startup sweep a list. Only
            // the Windows sweep reads it, so a fake runtime under test writes nothing. Engine-owned paths only (MxcGrantJournal).
            if (OperatingSystem.IsWindows())
            {
                MxcGrantJournal.Record(MxcGrantJournal.DefaultPath,
                    [.. mxcRequest.Filesystem?.ReadwritePaths ?? [], .. mxcRequest.Filesystem?.ReadonlyPaths ?? []],
                    MxcGrantJournal.EngineOwnedRoots,
                    _logger);
            }

            var spawned = await _mxcRuntime.SpawnAsync(mxcRequest, cancellationToken);
            return new MxcSandboxChildProcess(new MxcChildProcess(spawned,
                _timeProvider,
                onStandardOutputLine is null ? null : line => onStandardOutputLine(line),
                line => onStandardErrorLine(line)));
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        if (onStandardOutputLine is not null)
        {
            process.OutputDataReceived += (_, eventArgs) => onStandardOutputLine(eventArgs.Data);
        }

        process.ErrorDataReceived += (_, eventArgs) => onStandardErrorLine(eventArgs.Data);
        try
        {
            process.Start();
            if (onStandardOutputLine is not null)
            {
                process.BeginOutputReadLine();
            }

            process.BeginErrorReadLine();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new ProcessSandboxChildProcess(process);
    }

    /// <summary>
    ///     A start failure the command path reports instead of throwing: a plain child that could not be exec'd, or ANY MXC spawn failure
    ///     (native error, policy guard refusal, missing runtime), except the caller's own cancellation.
    /// </summary>
    private static bool IsLaunchFailure(Exception exception, SandboxLaunchDescriptor launch, CancellationToken cancellationToken)
    {
        if (exception is Win32Exception or InvalidOperationException)
        {
            return true;
        }

        return launch.MxcRequest is not null && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);
    }

    private string DescribeLaunchFailure(Exception exception, SandboxLaunchDescriptor launch)
    {
        if (launch.MxcRequest is null)
        {
            return "The sandbox command could not be launched.";
        }

        _logger.LogError(exception, "The MXC AppContainer boundary refused or failed the sandbox launch; the command was not run.");
        return $"The sandbox command could not be launched under the AppContainer boundary ({exception.Message}).";
    }

    /// <summary>
    ///     Registers the orphan-reaper marker for a command that is ABOUT to be launched into a transient scope,
    ///     claiming the unit name before <c>systemd-run</c> can create it.
    /// </summary>
    /// <returns>The marker id, or <see langword="null" /> when the store could not record it.</returns>
    /// <remarks>
    ///     It carries no pid — there is no child yet — which is what makes it safe to write this early: the reaper's
    ///     group-signalling path refuses a marker without one, so a pending marker can only ever protect the scope,
    ///     never authorise a kill.
    /// </remarks>
    private string? PreRegisterProcessMarker(JailState state, SandboxLaunchDescriptor launch)
    {
        return _markerStore.Write(new SandboxProcessMarker
        {
            SandboxId = state.Handle.SandboxId,
            ProcessGroupId = null,
            LeaderStartTicks = null,
            ScopeUnitName = launch.ScopeUnitName,
            JailPath = state.JailRoot,
            PreserveJail = state.PreserveJailRoot,
            OwnerProcessId = Environment.ProcessId,
            CreatedAt = _timeProvider.GetUtcNow()
        });
    }

    /// <summary>
    ///     Completes the marker for a just-started child: fills in the pid of a pre-registered one, or writes a fresh
    ///     one for a launch that had no scope to claim. Returns the marker id, or <see langword="null" /> when there is
    ///     nothing reapable to record.
    /// </summary>
    private string? CompleteProcessMarker(JailState state, ISandboxChildProcess process, SandboxLaunchDescriptor launch, string? pendingMarkerId)
    {
        var marker = BuildProcessMarker(state, process, launch);
        if (marker is null)
        {
            if (pendingMarkerId is not null)
            {
                _markerStore.Delete(pendingMarkerId);
            }

            return null;
        }

        if (pendingMarkerId is null)
        {
            return _markerStore.Write(marker);
        }

        // Keep the pre-registered identity: replacing it with a second file would leave the pending one behind, and
        // the teardown path below deletes exactly one id.
        _markerStore.Update(pendingMarkerId, marker);

        return pendingMarkerId;
    }

    /// <summary>Builds the orphan-reaper marker for a just-started child.</summary>
    /// <returns><see langword="null" /> when the launch produced nothing the reaper could act on.</returns>
    /// <remarks>
    ///     The pid is recorded ONLY when the child was launched under <c>setsid</c>, because the reaper signals with
    ///     <c>kill(-pgid)</c> and the pid is a process-group id only in that case. A non-leader pid would have the
    ///     reaper signal whatever group that pid belonged to — in the worst case the worker's own — so the absence of
    ///     the mechanism must mean the absence of a pid, not a guess.
    /// </remarks>
    private SandboxProcessMarker? BuildProcessMarker(JailState state, ISandboxChildProcess process, SandboxLaunchDescriptor launch)
    {
        int? processGroupId = null;
        long? leaderStartTicks = null;

        if (launch.AppliedProcessGroup)
        {
            try
            {
                var processId = process.Id;

                // The pid-reuse guard is only as good as the start time recorded alongside it; without one, record no
                // pid at all rather than a group id the reaper could not verify before signalling.
                if (new LinuxSandboxProcessGroupKiller(_timeProvider).GetProcessStartTicks(processId) is { } startTicks)
                {
                    processGroupId = processId;
                    leaderStartTicks = startTicks;
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited before its pid could be read — nothing to signal, so nothing to record.
            }
        }

        if (processGroupId is null && launch.ScopeUnitName is null)
        {
            return null;
        }

        return new SandboxProcessMarker
        {
            SandboxId = state.Handle.SandboxId,
            ProcessGroupId = processGroupId,
            ScopeUnitName = launch.ScopeUnitName,
            LeaderStartTicks = leaderStartTicks,
            JailPath = state.JailRoot,
            PreserveJail = state.PreserveJailRoot,
            OwnerProcessId = Environment.ProcessId,
            CreatedAt = _timeProvider.GetUtcNow()
        };
    }

    /// <summary>Tears down everything one launch started, in order of decreasing reach.</summary>
    /// <remarks>
    ///     First the scope's CGROUP, the only complete kill for a command in a PID namespace; then the process GROUP of a group
    ///     leader, which catches a detached descendant. <see cref="ISandboxChildProcess.Kill" /> has already run; these are the
    ///     best-effort layers under it. An MXC launch has neither: MXC's kill took the contained tree, and no Linux killer may be
    ///     pointed at a Windows pid.
    /// </remarks>
    private async Task TerminateLaunchAsync(SandboxLaunchDescriptor launch, ISandboxChildProcess process)
    {
        if (launch.MxcRequest is not null)
        {
            return;
        }

        if (launch.ScopeUnitName is { } unitName
            && SandboxScopeUnitKiller.TryCreate(_launcher.Containment.FilesystemIsolation) is { } scopeKiller)
        {
            try
            {
                // Explicitly not propagating: best-effort teardown must finish even when the run was cancelled.
                await scopeKiller.KillAsync(unitName, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Best-effort sandbox scope kill failed for unit {Unit}.", unitName);
            }
        }

        if (!launch.AppliedProcessGroup || !OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            // Explicitly not propagating: best-effort teardown must finish even when the run was cancelled.
            await new LinuxSandboxProcessGroupKiller(_timeProvider).KillProcessGroupAsync(process.Id, CancellationToken.None);
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
    ///     Resolves the jail-growth ceiling for one sandbox: the node-wide
    ///     <see cref="LocalContainerOptions.MaxJailDiskBytes" />, tightened by this sandbox's optional
    ///     <see cref="SandboxCreateRequest.MaxJailDiskBytes" />.
    /// </summary>
    /// <remarks>
    ///     TIGHTEN-ONLY, deliberately: the node-wide value is the operator's ceiling on what any sandbox on this machine
    ///     may write, so a create request may ask for less than it but never for more. The same asymmetry keeps a
    ///     request from re-enabling a watchdog the operator disabled with a non-positive node-wide value — a bigger
    ///     number never wins, so a disabled ceiling stays disabled.
    /// </remarks>
    private long ResolveJailDiskCeiling(JailState state)
    {
        return state.MaxJailDiskBytes is { } requested && requested < _maxJailDiskBytes
            ? requested
            : _maxJailDiskBytes;
    }

    /// <summary>
    ///     Captures (or reads back) this sandbox's jail occupancy baseline, or returns <see langword="null" /> when the
    ///     watchdog does not apply to it — a non-positive ceiling, or an engine-managed trusted host workspace.
    /// </summary>
    /// <remarks>
    ///     Called BEFORE the child is started, and that ordering is the whole point: the walk must see the jail holding
    ///     only what the ENGINE staged. Taken after the launch, a command whose first act is a write has its own bytes
    ///     measured INTO the baseline — permanently, the baseline being per sandbox — so they are free for it and for
    ///     every command after it, and a ceiling smaller than that first write can never fire. Only the first command
    ///     in a sandbox pays for the walk.
    /// </remarks>
    private static long? CaptureJailOccupancyBaseline(JailState state, long ceiling)
    {
        if (ceiling <= 0 || state.PreserveJailRoot)
        {
            return null;
        }

        var jailRoot = state.JailRoot;
        return state.GetOrCaptureOccupancyBaseline(() => MeasureDirectoryBytes(jailRoot, long.MaxValue));
    }

    /// <summary>
    ///     Starts the jail disk watchdog for one command, or returns a no-op when it does not apply; cancelling
    ///     <paramref name="diskCapSource" /> is what unblocks the command's wait and routes it to the over-cap result.
    /// </summary>
    /// <remarks>
    ///     The ceiling bounds the SANDBOX's occupancy above the baseline captured before its first command
    ///     (<see cref="CaptureJailOccupancyBaseline" />), never one command's growth, so a command that starts in an
    ///     over-full jail is terminated before its first tick. It is a best-effort sum of VISIBLE file lengths sampled
    ///     every two seconds and NOT a quota: an unlink-then-write loop, a burst between ticks and a sparse file each
    ///     evade it. Skipped for a trusted host workspace, which is the user's own checkout.
    /// </remarks>
    private IDisposable StartJailDiskWatchdog(JailState state,
        long ceiling,
        long? occupancyBaseline,
        CancellationTokenSource diskCapSource,
        CancellationToken commandToken)
    {
        // The baseline is null exactly when the watchdog does not apply — CaptureJailOccupancyBaseline made that call
        // against the same ceiling and the same state before the child was launched.
        if (ceiling <= 0 || state.PreserveJailRoot || occupancyBaseline is not { } baseline)
        {
            return new NoOpDisposable();
        }

        var watchdogSource = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
        var jailRoot = state.JailRoot;

        _ = Task.Run(async () =>
        {
            try
            {
                // A coarse interval: a safety net against a runaway writer, not a byte-accurate meter. The body runs BEFORE the
                // first wait, so a jail an earlier command already filled past the ceiling is caught at once.
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                do
                {
                    // Summing stops as soon as the ceiling is passed, so the cost stays bounded even when a command is
                    // actively filling the jail.
                    var occupancy = MeasureDirectoryBytes(jailRoot, baseline + ceiling);
                    if (occupancy - baseline > ceiling)
                    {
                        _logger.LogWarning("Sandbox command exceeded the jail disk ceiling of {Ceiling} bytes (jail occupancy {Occupancy} bytes over a baseline of {Baseline}); terminating it.",
                            ceiling,
                            occupancy,
                            baseline);
                        await diskCapSource.CancelAsync();
                        return;
                    }
                } while (await timer.WaitForNextTickAsync(watchdogSource.Token));
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

    /// <summary>
    ///     One live <see cref="ISandboxInteractiveProcess" />: the child's standard streams, plus everything needed to
    ///     tear it down.
    /// </summary>
    /// <remarks>
    ///     Disposal runs the SAME layers <c>ExecuteAsync</c> runs on an abnormal exit — tree-kill, then the scope
    ///     cgroup and the process group through <see cref="TerminateLaunchAsync" /> — and then removes the in-flight
    ///     entry and the reaper marker, so a released process leaves nothing for the next startup sweep.
    /// </remarks>
    private sealed class InteractiveProcess : ISandboxInteractiveProcess
    {
        private readonly CancellationTokenSource _cancelSource;
        private readonly string _executionId;
        private readonly SandboxLaunchDescriptor _launch;
        private readonly string? _markerId;
        private readonly ISandboxChildProcess _process;
        private static readonly TimeSpan StderrDrainWait = TimeSpan.FromSeconds(2);

        private readonly ProcessSandboxRuntimeProvider _provider;
        private readonly JailState _state;
        private readonly SandboxStderrTail _stderrTail;
        private int _disposed;

        public InteractiveProcess(ProcessSandboxRuntimeProvider provider,
            JailState state,
            string executionId,
            ISandboxChildProcess process,
            SandboxLaunchDescriptor launch,
            string? markerId,
            CancellationTokenSource cancelSource,
            SandboxStderrTail stderrTail)
        {
            _provider = provider;
            _state = state;
            _executionId = executionId;
            _process = process;
            _launch = launch;
            _markerId = markerId;
            _cancelSource = cancelSource;
            _stderrTail = stderrTail;
        }

        public Stream StandardInput => _process.StandardInput;

        public Stream StandardOutput => _process.StandardOutput;

        public int? ExitCode => Volatile.Read(ref _disposed) == 0 ? _process.ExitCode : null;

        public IReadOnlyList<string> Warnings => Volatile.Read(ref _disposed) == 0 ? _process.Warnings : [];

        public async Task<string?> GetStandardErrorTailAsync()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                // Waits for exit AND the stderr reader's end of stream, so the last lines land; bounded, and never kills. Still running
                // or torn down concurrently leaves the tail so far, the best answer there is.
                using var drain = new CancellationTokenSource(StderrDrainWait, _provider._timeProvider);
                _ = await _process.TryWaitForExitAsync(drain.Token);
            }

            return _stderrTail.Snapshot();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
            {
                return;
            }

            _process.Kill();
            await _provider.TerminateLaunchAsync(_launch, _process);
            if (_launch.MxcRequest is not null)
            {
                // Reap, bounded: MXC clears the run's DACL grants once the workload has exited (ClearPolicyOnExit).
                using var reap = new CancellationTokenSource(StderrDrainWait, _provider._timeProvider);
                _ = await _process.TryWaitForExitAsync(reap.Token);
            }

            _ = _state.InFlight.TryRemove(_executionId, out _);
            if (_markerId is not null)
            {
                _provider._markerStore.Delete(_markerId);
            }

            _process.Dispose();
            _cancelSource.Dispose();
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
            // Nothing to release: the watchdog did not start.
        }
    }

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
    ///     A thread-safe string accumulator with a hard ceiling measured in real UTF-8 BYTES, matching the
    ///     <c>…Bytes</c> budget name.
    /// </summary>
    /// <remarks>
    ///     The event pump can fire from a pool thread, so appends lock; once the byte cap is reached further data is
    ///     discarded, while the pump keeps draining the pipe so the child never blocks. A multibyte line that would
    ///     cross the cap is truncated at a UTF-8 rune boundary, so the accumulated string never exceeds the cap in
    ///     encoded bytes.
    /// </remarks>
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

    private enum InFlightRegistration
    {
        Registered,
        SandboxDead,
        DuplicateExecutionId
    }
}
