namespace XE_Local_AI_Engine.Client.Services.Compute.Implementation;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Runs a model-supplied script through the AGENT-role sandbox provider and renders exit code, stdout and stderr the
///     way <c>HostProcessExecutor.FormatResult</c> does, so every command-shaped tool result in this product reads the
///     same to a model.
/// </summary>
/// <remarks>
///     The sandbox is keyed on its own <see cref="RuntimeProfile" />, which keeps it a SEPARATE jail from AgentHome's,
///     and on the invocation id, which makes it per CALL — killed when the call returns, taking the HOME/TMPDIR scratch
///     inside it with it, which is what makes the advertised statelessness true. No execution lease is taken: the lease
///     serializes AgentHome's single workspace and this sandbox has none. The FILESYSTEM BOUNDARY is unconditional and
///     fails the call closed. All of it, and what the boundary is NOT: docs/wiki/19-compute-tools.md, "2. Execution path — process sandbox, in an isolated mount namespace".
/// </remarks>
internal sealed class ComputeToolGateway : IComputeToolGateway
{
    /// <summary>
    ///     The sandbox runtime profile this tool's jail is keyed on — deliberately not AgentHome's. This is the create
    ///     request's profile verbatim; the ATTACH KEY carries it with the invocation id appended, so every call gets
    ///     its own jail (see <see cref="BuildCreateRequest" />).
    /// </summary>
    internal const string RuntimeProfile = "compute-python";

    /// <summary>
    ///     The attach-key generation for the compute jail. Bump it only to force a fresh compute jail.
    /// </summary>
    /// <remarks>
    ///     Its OWN constant rather than AgentHome's manifest version, because this jail has no manifest: borrowing that
    ///     number would make an AgentHome layout change silently re-key a sandbox that shares nothing with it.
    /// </remarks>
    private const int SandboxGeneration = 1;

    /// <summary>The marker every capped stream in this product ends with, so a model reads one convention.</summary>
    private const string Marker = "…[output truncated]";

    private const string StderrLabel = "\nstderr:\n";

    /// <summary>
    ///     The jail subdirectory the sandbox presents as <see cref="SandboxIsolatedPaths.Home" />, named here as the
    ///     sandbox-relative path the provider's reset operation takes.
    /// </summary>
    /// <remarks>
    ///     Below the jail deliberately: the provider's disk watchdog meters the JAIL, so a scratch directory anywhere
    ///     else is unmetered space a script can fill while the ceiling it was given reports nothing.
    /// </remarks>
    private const string HomeDirectoryName = "home";

    /// <summary>
    ///     The jail subdirectory behind <see cref="SandboxIsolatedPaths.Temp" />. A second directory rather than one
    ///     shared with the home: a script clearing its <c>tempfile</c> leftovers must not wipe its own home.
    /// </summary>
    private const string TempDirectoryName = ".tmp";

    private readonly IComputePythonEnvironment _environment;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILogger<ComputeToolGateway> _logger;
    private readonly LocalContainerOptions _nodeOptions;
    private readonly ComputeOptions _options;
    private readonly int _maxToolResultCharacters;
    private readonly IAgentSandboxRuntimeProvider _provider;

    public ComputeToolGateway(IAgentSandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        IComputePythonEnvironment environment,
        IOptions<ComputeOptions> options,
        IOptions<LocalContainerOptions> nodeOptions,
        IOptions<AgentToolPipelineOptions> pipelineOptions,
        ILogger<ComputeToolGateway> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ArgumentNullException.ThrowIfNull(nodeOptions);
        _nodeOptions = nodeOptions.Value;
        ArgumentNullException.ThrowIfNull(pipelineOptions);
        _maxToolResultCharacters = pipelineOptions.Value.MaxToolResultCharacters;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     The model-facing projection: <see cref="ExecuteDetailedAsync" /> with no ceiling requirement, rendered as the
    ///     string a model reads. One execution path, two projections — a second entry point is how a refusal reachable
    ///     one way stops being reachable the other.
    /// </summary>
    public async Task<string> ExecuteAsync(ComputeRunToolRequest request, CancellationToken cancellationToken = default)
    {
        var outcome = await ExecuteDetailedAsync(request, requireResourceLimits: false, cancellationToken);
        return outcome.Result is { } result
            ? FormatResult(result)
            : outcome.RefusalMessage ?? "run_python rejected.";
    }

    public async Task<ComputeExecutionOutcome> ExecuteDetailedAsync(ComputeRunToolRequest request,
        bool requireResourceLimits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // The node kill-switch, read HERE and nowhere else: a copy in a handler is a property of one CALLER, so a second
        // caller reaching this gateway would execute code on a node that never opted in. The sentence is the one the model reads.
        if (!_options.Enabled)
        {
            return ComputeExecutionOutcome.Refused(ComputeRefusalCodes.ComputeDisabled,
                "The Python compute tool is disabled on this node (Compute:Enabled=false).");
        }

        // Likewise the request validation, and for the same reason: it is now authoritative for every caller rather
        // than for whichever one remembered to call the validator first.
        var validationErrors = ComputeRunToolRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return ComputeExecutionOutcome.Refused(ComputeRefusalCodes.InvalidRequest,
                $"run_python arguments are invalid: {string.Join(" ", validationErrors)}");
        }

        // Non-null by the validation immediately above, which is the only thing that establishes it.
        var code = request.Code!;

        // The boundary is ADVERTISED, so it fails closed HERE — before the interpreter is provisioned, before the node identity is read and
        // before a jail exists — and it subsumes an egress-capability check. See docs/wiki/19-compute-tools.md, "2.1 Execution flow", step 2.
        if (!_provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            _logger.LogWarning(
                "run_python refused: the '{Provider}' sandbox provider cannot isolate the compute sandbox filesystem on this host, and that boundary is not optional. Install bubblewrap (bwrap) together with the user-namespace support the sandbox containment probe reports as missing, or leave Compute:Enabled off.",
                _provider.ProviderName);
            return ComputeExecutionOutcome.Refused(ComputeRefusalCodes.NoIsolation,
                "run_python rejected: this node cannot isolate the compute sandbox filesystem, and the tool never runs a script that could read or write the rest of the machine.");
        }

        // The ceilings are CAPABILITY-gated (SandboxResourceCeilings.Resolve returns null when the backend cannot impose them), so on a host
        // with no working systemd user scope a script runs unbounded. Acceptable for a call a human approved, not for unattended code — so only a caller that asks is refused.
        if (requireResourceLimits && !_provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits))
        {
            _logger.LogWarning("A compute execution requiring enforceable resource ceilings was refused: the '{Provider}' sandbox provider cannot impose CPU, memory or process limits on this host.",
                _provider.ProviderName);
            return ComputeExecutionOutcome.Refused(ComputeRefusalCodes.NoResourceLimits,
                "run_python rejected: this node's sandbox cannot enforce CPU, memory and process ceilings, and unattended execution is not run without them.");
        }

        // One id for everything this invocation owns: its jail and its execution. Killing the jail discards every byte the script
        // wrote, scratch included. The provisioned venv lives under the compute cache root, read-only to the script, so it survives untouched.
        var invocationId = Guid.NewGuid().ToString("N");
        SandboxHandle? handle = null;
        try
        {
            var runtime = await _environment.GetRuntimeAsync(cancellationToken);
            var identity = await _identityProvider.GetAsync(cancellationToken);
            handle = await _provider.CreateOrAttachAsync(BuildCreateRequest(identity, runtime, invocationId), cancellationToken);
            if (string.IsNullOrEmpty(handle.WorkingRoot))
            {
                // Fails closed for the same reason the boundary check does: the jail backs /work, so a provider that cannot name it has
                // nowhere to put the script's single writable tree, and the disk ceiling meters that directory and nothing else.
                _logger.LogWarning(
                    "run_python refused: the '{Provider}' sandbox provider reports no jail root, so there is no host directory to back the sandbox's writable tree or to meter against the disk ceiling.",
                    _provider.ProviderName);
                return ComputeExecutionOutcome.Refused(ComputeRefusalCodes.NoJailRoot,
                    "run_python rejected: this node's sandbox cannot give the script a working directory of its own.");
            }

            await EnsureScratchAsync(handle, cancellationToken);
            var result = await _provider.ExecuteAsync(handle, BuildCommandRequest(runtime.InterpreterPath, code, invocationId, request.TimeoutSeconds), cancellationToken);
            return ComputeExecutionOutcome.Executed(result);
        }
        catch (ComputeEnvironmentException exception)
        {
            // Model-safe by contract (see the exception type), so it is the one class of failure surfaced verbatim.
            return ComputeExecutionOutcome.Refused(ComputeRefusalCodes.EnvironmentUnavailable,
                $"run_python rejected: {exception.Message}");
        }
        catch (SandboxCapabilityNotSupportedException exception)
        {
            _logger.LogWarning(exception, "The compute sandbox could not be created with the requested containment.");
            return ComputeExecutionOutcome.Refused(ComputeRefusalCodes.ContainmentUnavailable,
                "run_python rejected: this node's sandbox cannot provide the containment the compute tool requires.");
        }
        finally
        {
            // CancellationToken.None deliberately: a cancelled or failed call is exactly when a script is most likely to have left
            // something behind. ONE teardown covers both writable surfaces, because the scratch sits under the jail root.
            if (handle is not null)
            {
                await KillQuietlyAsync(handle);
            }
        }
    }

    /// <summary>
    ///     Materializes the two jail subdirectories the sandbox presents as <c>HOME</c> and <c>TMPDIR</c>, through the
    ///     provider's own reset operation rather than a host <c>Directory.CreateDirectory</c>.
    /// </summary>
    /// <remarks>
    ///     The provider's surface is the one that applies the jail's path and symlink guards, and the only one that
    ///     stays correct for a provider whose sandbox paths are not host paths. Nothing is returned, because under
    ///     isolation the paths the CHILD sees are fixed — <see cref="SandboxIsolatedPaths.Home" /> and
    ///     <see cref="SandboxIsolatedPaths.Temp" />, not host paths at all. What the call buys is that both directories
    ///     EXIST and are empty before the command starts, rather than being assumed of a provider's launch path.
    /// </remarks>
    private async Task EnsureScratchAsync(SandboxHandle handle, CancellationToken cancellationToken)
    {
        await _provider.ResetDirectoryAsync(handle, HomeDirectoryName, cancellationToken);
        await _provider.ResetDirectoryAsync(handle, TempDirectoryName, cancellationToken);
    }

    /// <summary>
    ///     Terminates the jail this call ran in, which is what deletes everything the script wrote below it.
    /// </summary>
    /// <remarks>
    ///     Failures are logged rather than thrown: the model already has its result, and replacing a completed run's
    ///     output with a teardown error would tell it the computation failed when it did not.
    /// </remarks>
    private async Task KillQuietlyAsync(SandboxHandle handle)
    {
        try
        {
            await _provider.KillAsync(handle, CancellationToken.None);
        }
        catch (Exception exception) when (exception is SandboxHandleInvalidException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The compute jail could not be torn down after the call.");
        }
    }

    /// <summary>
    ///     Builds the create request, with an attach key carrying <paramref name="invocationId" /> so it is unique per
    ///     call.
    /// </summary>
    /// <remarks>
    ///     The key is what the registry attaches BY, so a constant one gives two overlapping <c>run_python</c> calls a
    ///     single live jail — one working directory between unrelated conversations, and whichever finishes first
    ///     killing it out from under the other. The registry already treats the runtime profile as the scope that lets
    ///     distinct jails coexist (it is hashed into the sandbox id), so widening it needs nothing to serialize. Only
    ///     different-OWNER jails are evicted on create, so two calls by the same owner never disturb each other.
    /// </remarks>
    private SandboxCreateRequest BuildCreateRequest(AgentHomeOwnerIdentity identity,
        ComputePythonRuntime runtime,
        string invocationId)
    {
        var capabilities = _provider.Capabilities;
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = identity.OwnerUserId,
                NodeId = identity.NodeId,
                ProviderName = _provider.ProviderName,
                RuntimeProfile = RuntimeProfile + "-" + invocationId,
                ManifestVersion = SandboxGeneration
            },
            RuntimeProfile = RuntimeProfile,
            // Unconditional: ExecuteAsync already refused the call if this provider cannot honor it. Unlike the ceilings below this is not a
            // preference a provider may quietly drop — a request naming it is rejected fail-closed, which is the whole reason it is safe to ask for.
            Isolation = SandboxIsolationMode.Filesystem,
            // The two smallest trees that make the interpreter run, and nothing else. Naming the compute cache root instead would hand the
            // script the uv download cache, the uv binary and the lockfile state marker too — see ComputePythonEnvironment.BuildRuntime.
            ReadOnlyTrees = runtime.ReadOnlyTrees,
            ThreadLimit = _options.ThreadLimit,
            // Still stated, though the isolated chain's --unshare-net enforces it and the registry no longer consults the separate egress
            // capability for an isolated request. It is the create request's default anyway; spelling it keeps the intent legible here.
            NetworkPolicy = SandboxNetworkPolicy.None,
            // Unconditional too, and it needs no capability check because it can only ASK FOR LESS than the node-wide jail ceiling, so a provider
            // that ignores it is no worse off. Inheriting the node-wide number lets one runaway write loop consume the whole allowance a workspace run is sized for.
            MaxJailDiskBytes = _options.MaxJailDiskBytes,
            // The node's ceilings, through the helper every sandbox create site shares. The numbers are Compute's own and are now every role's, so
            // raising them here raises them for AgentHome and Development Mode too. See SandboxResourceCeilings for that trade.
            ResourceLimits = SandboxResourceCeilings.Resolve(SandboxWorkloads.RunPython, capabilities, _options, _nodeOptions)
        };
    }

    private SandboxCommandRequest BuildCommandRequest(string interpreter, string code, string invocationId, int? requestedTimeoutSeconds)
    {
        var timeoutSeconds = requestedTimeoutSeconds is { } requested && requested > 0 && requested < _options.TimeoutSeconds
            ? requested
            : _options.TimeoutSeconds;
        return new SandboxCommandRequest
        {
            ExecutionId = "compute-" + invocationId,
            // The venv's own interpreter at its host path, which resolves inside because the venv is bound read-only AT ITS OWN CANONICAL PATH — also why
            // the tree list must never bind somewhere tidier. No working directory is named: the sandbox's writable tree IS the working directory.
            Executable = interpreter,
            // `-I` is isolated mode: no PYTHONPATH, no user site-packages, no script-directory import, so the import surface is the provisioned lockfile
            // closure and not the working directory or the operator's environment. `-` reads the program from stdin, so it never hits disk or argv.
            Arguments = ["-I", "-"],
            StandardInput = code,
            Environment = BuildEnvironment(),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    /// <summary>
    ///     The environment the script runs with, expressed in the SANDBOX's view of the filesystem: every path is an
    ///     in-jail path (<see cref="SandboxIsolatedPaths" />), because under isolation a host path names nothing the
    ///     child can reach.
    /// </summary>
    /// <remarks>
    ///     The isolated chain sets the same variables in its own fixed allow-list and these are emitted after it, so this is a deliberate
    ///     restatement: that a script's <c>HOME</c> and <c>TMPDIR</c> are per-invocation, discarded on return and charged to the disk
    ///     ceiling is THIS tool's promise to the model, and a disagreement with the provider's view surfaces here rather than as a script
    ///     caching somewhere unmetered. The numeric-library thread pinning is NOT restated: it derives from
    ///     <see cref="SandboxCreateRequest.ThreadLimit" />, and naming it twice would let it and the CPU ceiling drift apart.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> BuildEnvironment()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = SandboxIsolatedPaths.Home,
            ["TMPDIR"] = SandboxIsolatedPaths.Temp,
            ["TMP"] = SandboxIsolatedPaths.Temp,
            ["TEMP"] = SandboxIsolatedPaths.Temp,
            // The venv is bound READ-ONLY, so a user-site directory could not be written even if one resolved; the
            // point of the variable is that the interpreter must not go looking for one it might find bound.
            ["PYTHONNOUSERSITE"] = "1",
            // Nothing re-reads a __pycache__ across calls (each run is a fresh stdin program), so writing one only
            // litters the jail against the disk watchdog.
            ["PYTHONDONTWRITEBYTECODE"] = "1"
        };
    }

    /// <summary>
    ///     Renders the outcome in the same shape <c>HostProcessExecutor.FormatResult</c> produces for a custom Command
    ///     tool, including the truncation marker, so a model that has learned to read one command result reads both.
    /// </summary>
    /// <remarks>
    ///     The WHOLE rendering fits <see cref="ComputeOptions.MaxOutputBytes" /> (or a tighter tool-result
    ///     budget), because the tool-result pipeline clips the END of anything longer, and the end is where the stderr
    ///     traceback is (F-61). Stderr may claim half the stream budget and keeps its TAIL; stdout keeps its head and
    ///     is trimmed first. Bytes bound characters, so a byte budget also fits the pipeline's character budget.
    /// </remarks>
    private string FormatResult(SandboxCommandResult result)
    {
        var builder = new StringBuilder();
        if (!result.Completed)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"The script did not finish within {_options.TimeoutSeconds}s and its process tree was terminated.\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"exit_code: {result.ExitCode}\n");
        builder.Append("stdout:\n");

        var budget = Math.Min(Math.Min(_options.MaxOutputBytes, _maxToolResultCharacters), ToolResultBudgetScope.Current ?? int.MaxValue);
        var streamBudget = Math.Max(0,
            budget - Encoding.UTF8.GetByteCount(builder.ToString()) - Encoding.UTF8.GetByteCount(StderrLabel) - (3 * (Encoding.UTF8.GetByteCount(Marker) + 1)));
        var stdoutBytes = Encoding.UTF8.GetByteCount(result.StandardOutput);
        var stderrBytes = Encoding.UTF8.GetByteCount(result.StandardError);
        var stdoutAllowed = Math.Min(stdoutBytes, streamBudget - Math.Min(stderrBytes, streamBudget / 2));
        var stderrAllowed = Math.Min(stderrBytes, streamBudget - stdoutAllowed);

        if (stdoutBytes > stdoutAllowed)
        {
            builder.Append(TruncateToUtf8ByteBudget(result.StandardOutput, stdoutAllowed)).Append(Marker);
        }
        else
        {
            builder.Append(result.StandardOutput);
            if (result.StandardOutputTruncated)
            {
                builder.Append(Marker);
            }
        }

        builder.Append(StderrLabel);
        if (stderrBytes > stderrAllowed)
        {
            builder.Append(Marker).Append('\n').Append(KeepUtf8Tail(result.StandardError, stderrAllowed));
        }
        else
        {
            builder.Append(result.StandardError);
        }

        if (result.StandardErrorTruncated)
        {
            builder.Append(Marker);
        }

        return builder.ToString();
    }

    /// <summary>Keeps the TAIL of a stream within a BYTE budget without ever splitting a rune.</summary>
    private static string KeepUtf8Tail(string value, int budget)
    {
        var excess = Encoding.UTF8.GetByteCount(value) - budget;
        var charIndex = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (excess <= 0)
            {
                break;
            }

            excess -= rune.Utf8SequenceLength;
            charIndex += rune.Utf16SequenceLength;
        }

        return value[charIndex..];
    }

    /// <summary>
    ///     Keeps the HEAD of a stream within a BYTE budget without ever splitting a rune.
    /// </summary>
    /// <remarks>
    ///     A plain <c>GetBytes</c>/<c>GetString</c> slice would split one at a multi-byte boundary. Same algorithm as
    ///     <c>HostProcessExecutor.CappedOutput</c>'s, for the same reason: the cap is in bytes and the value is text.
    /// </remarks>
    private static string TruncateToUtf8ByteBudget(string value, int budget)
    {
        var used = 0;
        var lastCharIndex = 0;
        var charIndex = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > budget)
            {
                break;
            }

            used += rune.Utf8SequenceLength;
            charIndex += rune.Utf16SequenceLength;
            lastCharIndex = charIndex;
        }

        return value[..lastCharIndex];
    }
}
