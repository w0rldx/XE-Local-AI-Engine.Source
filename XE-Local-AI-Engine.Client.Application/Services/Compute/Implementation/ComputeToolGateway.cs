namespace XE_Local_AI_Engine.Client.Services.Compute.Implementation;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Runs a model-supplied script through the AGENT-role sandbox provider and renders exit code, stdout and stderr the
///     way <c>HostProcessExecutor.FormatResult</c> does, so every command-shaped tool result in this product reads the
///     same to a model.
/// </summary>
/// <remarks>
///     <para>
///         The sandbox is keyed on its own <see cref="RuntimeProfile" />, which is what keeps it a SEPARATE jail from
///         AgentHome's: the attach key hashes the runtime profile, so <c>compute-python</c> and
///         <c>dotnet-agent-home</c> never collide, and a compute script therefore cannot read, write or corrupt a
///         workspace an AgentHome run has staged. It also means no execution lease is taken — the lease serializes
///         access to AgentHome's single workspace, and this sandbox has no workspace to serialize.
///     </para>
///     <para>
///         The jail is also per INVOCATION — keyed on the invocation id, killed when the call returns, which deletes
///         the jail root the script ran in and, with it, the HOME/TMPDIR scratch that lives INSIDE that root. The tool
///         advertises itself to the model as stateless, and only a per-call key plus that teardown makes it true: a
///         constant key let a later call read an earlier script's files, and let two CONCURRENT calls share one jail
///         and one working directory, with the first to finish tearing it down mid-run under the second. Only writable
///         state is discarded; the expensive part, the uv-provisioned venv, lives outside the jail and is untouched.
///     </para>
///     <para>
///         The FILESYSTEM BOUNDARY is unconditional and fails the call closed. Every invocation asks for
///         <see cref="SandboxIsolationMode.Filesystem" />, so the script runs in a mount namespace that does not
///         contain the host filesystem at all: a read-only <c>/usr</c>, an invented <c>/etc</c>, the two interpreter
///         trees bound read-only, and one writable directory which is this call's own jail. A node whose sandbox
///         provider cannot deliver that is REFUSED before anything is provisioned or created — "sandboxed" is what
///         the tool's description promises the model and what the user approved the call on, and a host that quietly
///         could not honour it would be the one case where the approval bought nothing.
///     </para>
///     <para>
///         Egress denial rides on the same mechanism: the isolated chain unshares the network namespace
///         unconditionally, and the containment probe proves it with a loopback connect that fails inside while
///         succeeding outside. The resource ceilings stay capability-gated, because they bound cost rather than
///         reachability — degrading them is visible in the containment log and costs no guarantee.
///     </para>
///     <para>
///         What the boundary is NOT is a kernel-hardened one: no seccomp filter, no LSM profile, and the disk ceiling
///         under it is a best-effort occupancy check rather than a quota. That is why the tool stays
///         <c>WriteExecute</c>, approval-required, off by default, and never offered to a cloud-hosted model.
///     </para>
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
    ///     The attach-key generation for the compute jail. It is its OWN constant rather than AgentHome's manifest
    ///     version because this jail has no manifest: borrowing that number would make an AgentHome layout change
    ///     silently re-key a sandbox that shares nothing with it. Bump this only to force a fresh compute jail.
    /// </summary>
    private const int SandboxGeneration = 1;

    /// <summary>The marker every capped stream in this product ends with, so a model reads one convention.</summary>
    private const string Marker = "…[output truncated]";

    /// <summary>
    ///     The jail subdirectory the sandbox presents as <see cref="SandboxIsolatedPaths.Home" />, named here as the
    ///     sandbox-relative path the provider's reset operation takes. Below the jail deliberately: the provider's
    ///     disk watchdog meters the JAIL, so a scratch directory anywhere else is unmetered space a script can fill
    ///     while the ceiling it was given reports nothing.
    /// </summary>
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
    private readonly IAgentSandboxRuntimeProvider _provider;

    public ComputeToolGateway(IAgentSandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        IComputePythonEnvironment environment,
        IOptions<ComputeOptions> options,
        IOptions<LocalContainerOptions> nodeOptions,
        ILogger<ComputeToolGateway> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ArgumentNullException.ThrowIfNull(nodeOptions);
        _nodeOptions = nodeOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ExecuteAsync(ComputeRunToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // The handler validates this before delegating; stating it here keeps the invariant visible rather than resting
        // on a null-forgiving operator at the call site.
        var code = request.Code
                   ?? throw new ArgumentException("The compute request carries no code.", nameof(request));

        // The boundary is ADVERTISED, so it fails closed — and it fails closed HERE, before the interpreter is
        // provisioned, before the node identity is read and before a jail exists. A refusal any later would have
        // downloaded and unpacked a Python closure onto a node that can never run it, and would have had to explain
        // itself from inside a half-built sandbox.
        //
        // This check REPLACES the egress-capability check that stood in this spot, and subsumes it: the isolated
        // chain's empty network namespace is bwrap's own --unshare-net, which the containment probe positively
        // controls with a loopback connect, so the separate unshare(1) mechanism the old check tested is no longer on
        // the path — gating on it would refuse a host that isolates perfectly well. The semantics are unchanged and
        // strictly stronger: the call is still refused rather than run with the operator's network, and now rather
        // than run with the operator's filesystem either.
        if (!_provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            _logger.LogWarning(
                "run_python refused: the '{Provider}' sandbox provider cannot isolate the compute sandbox filesystem on this host, and that boundary is not optional. Install bubblewrap (bwrap) together with the user-namespace support the sandbox containment probe reports as missing, or leave Compute:Enabled off.",
                _provider.ProviderName);
            return "run_python rejected: this node cannot isolate the compute sandbox filesystem, and the tool never runs a script that could read or write the rest of the machine.";
        }

        // One id for everything this invocation owns: its jail and its execution. Both are per call, and killing the
        // jail discards every byte the script wrote — including its scratch, which lives inside it. That is what makes
        // the advertised statelessness real rather than a claim. The provisioned venv is NOT here: it lives under the
        // compute cache root, costs seconds to build, and is read-only to the script, so it survives untouched.
        var invocationId = Guid.NewGuid().ToString("N");
        SandboxHandle? handle = null;
        try
        {
            var runtime = await _environment.GetRuntimeAsync(cancellationToken).ConfigureAwait(false);
            var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            handle = await _provider.CreateOrAttachAsync(BuildCreateRequest(identity, runtime, invocationId), cancellationToken)
                                    .ConfigureAwait(false);
            if (string.IsNullOrEmpty(handle.WorkingRoot))
            {
                // Fails closed for the same reason the boundary check does. The jail is what backs /work inside the
                // sandbox, so a provider that cannot name it has nowhere to put the script's single writable tree —
                // and the disk ceiling this call was granted meters that directory and nothing else. No agent-role
                // provider that actually executes anything hits this.
                _logger.LogWarning(
                    "run_python refused: the '{Provider}' sandbox provider reports no jail root, so there is no host directory to back the sandbox's writable tree or to meter against the disk ceiling.",
                    _provider.ProviderName);
                return "run_python rejected: this node's sandbox cannot give the script a working directory of its own.";
            }

            await EnsureScratchAsync(handle, cancellationToken).ConfigureAwait(false);
            var result = await _provider.ExecuteAsync(handle, BuildCommandRequest(runtime.InterpreterPath, code, invocationId), cancellationToken)
                                        .ConfigureAwait(false);
            return FormatResult(result);
        }
        catch (ComputeEnvironmentException exception)
        {
            // Model-safe by contract (see the exception type), so it is the one class of failure surfaced verbatim.
            return $"run_python rejected: {exception.Message}";
        }
        catch (SandboxCapabilityNotSupportedException exception)
        {
            _logger.LogWarning(exception, "The compute sandbox could not be created with the requested containment.");
            return "run_python rejected: this node's sandbox cannot provide the containment the compute tool requires.";
        }
        finally
        {
            // CancellationToken.None deliberately: a cancelled or failed call is exactly when a script is most likely to
            // have left something behind, so the teardown must still run. ONE teardown covers both writable surfaces
            // now that the scratch sits under the jail root — a cancelled call cannot leave a scratch directory behind
            // that the jail kill missed, because there is no longer a directory outside the jail to miss.
            if (handle is not null)
            {
                await KillQuietlyAsync(handle).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Materializes the two jail subdirectories the sandbox presents as <c>HOME</c> and <c>TMPDIR</c>, through the
    ///     provider's own reset operation rather than a host <c>Directory.CreateDirectory</c>: that is the surface
    ///     that applies the jail's path and symlink guards, and it is the only one that stays correct for a provider
    ///     whose sandbox paths are not host paths.
    ///     <para>
    ///         Nothing is returned, because under isolation the paths the CHILD sees are fixed — the sandbox's own
    ///         <see cref="SandboxIsolatedPaths.Home" /> and <see cref="SandboxIsolatedPaths.Temp" />, not host paths
    ///         at all. What this call still buys is that both directories EXIST and are empty before the command
    ///         starts, asked for through the provider rather than assumed of whatever a provider's launch path
    ///         happens to create.
    ///     </para>
    /// </summary>
    private async Task EnsureScratchAsync(SandboxHandle handle, CancellationToken cancellationToken)
    {
        await _provider.ResetDirectoryAsync(handle, HomeDirectoryName, cancellationToken).ConfigureAwait(false);
        await _provider.ResetDirectoryAsync(handle, TempDirectoryName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Terminates the jail this call ran in, which is what deletes everything the script wrote below it. Failures
    ///     are logged rather than thrown: the model already has its result by now, and replacing a completed run's
    ///     output with a teardown error would tell it the computation failed when it did not.
    /// </summary>
    private async Task KillQuietlyAsync(SandboxHandle handle)
    {
        try
        {
            await _provider.KillAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SandboxHandleInvalidException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The compute jail could not be torn down after the call.");
        }
    }

    /// <summary>
    ///     Builds the create request. The attach key carries <paramref name="invocationId" /> so it is unique per call:
    ///     the key is what the registry attaches BY, so a constant one made two overlapping run_python calls share a
    ///     single live jail — one working directory between unrelated conversations, and, now that teardown is per call,
    ///     whichever finished first killing the jail out from under the other. The registry already treats the runtime
    ///     profile as the scope that lets distinct jails coexist (it is hashed into the sandbox id), so widening it is
    ///     the whole fix and nothing needs to serialize. Only different-OWNER jails on a node are evicted on create, so
    ///     two calls by the same owner never disturb each other.
    /// </summary>
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
            // Unconditional: ExecuteAsync already refused the call if this provider cannot honor it. Unlike the
            // ceilings below this is not a preference a provider may quietly drop — a request naming it is rejected
            // fail-closed by a provider that cannot deliver it, which is the whole reason it is safe to ask for.
            Isolation = SandboxIsolationMode.Filesystem,
            // The two smallest trees that make the interpreter run, and nothing else. Naming the compute cache root
            // instead would have been one line shorter and would have handed the script the uv download cache, the
            // uv binary and the lockfile state marker as well — see ComputePythonEnvironment.BuildRuntime.
            ReadOnlyTrees = runtime.ReadOnlyTrees,
            ThreadLimit = _options.ThreadLimit,
            // Still stated, though the isolated chain's --unshare-net is what enforces it and the registry no longer
            // consults the separate egress capability for an isolated request. It is the create request's default
            // anyway, and spelling it keeps the intent legible at the one place a reader looks for it.
            NetworkPolicy = SandboxNetworkPolicy.None,
            // Unconditional too, and for the same reason it needs no capability check: it can only ASK FOR LESS than
            // the node-wide jail ceiling the provider would otherwise apply, so a provider that ignores it is no worse
            // off than before. A script doing arithmetic writes almost nothing, and inheriting the node-wide number let
            // one runaway `open(..., "w")` loop consume the whole allowance a workspace run is sized for.
            MaxJailDiskBytes = _options.MaxJailDiskBytes,
            // The node's ceilings, through the helper every sandbox create site now shares. This site used to derive
            // them inline and was the ONLY one that asked; the numbers are still Compute's own, and they are now every
            // role's, so raising them here raises them for AgentHome and Development Mode too. See
            // SandboxResourceCeilings for that trade and for what the defaults cost a build.
            ResourceLimits = SandboxResourceCeilings.Resolve(SandboxWorkloads.RunPython, capabilities, _options, _nodeOptions)
        };
    }

    private SandboxCommandRequest BuildCommandRequest(string interpreter, string code, string invocationId)
    {
        return new SandboxCommandRequest
        {
            ExecutionId = "compute-" + invocationId,
            // The venv's own interpreter, at its host path. Inside the sandbox that path resolves because the venv is
            // bound read-only AT ITS OWN CANONICAL PATH — which is also why the tree list must never be rewritten to
            // bind somewhere tidier. No working directory is named: the sandbox's writable tree IS the working
            // directory, and asking for a subdirectory of it would only give the script one more thing to escape.
            Executable = interpreter,
            // `-I` is isolated mode: no PYTHONPATH, no user site-packages, no script-directory import. It is what keeps
            // the interpreter's import surface the provisioned lockfile closure rather than whatever happens to sit in
            // the working directory or the operator's environment. `-` reads the program from stdin, so the script is
            // never written to disk and never becomes an argv the process table exposes.
            Arguments = ["-I", "-"],
            StandardInput = code,
            Environment = BuildEnvironment(),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
    }

    /// <summary>
    ///     The environment the script runs with, expressed in the SANDBOX's view of the filesystem. Every path here is
    ///     an in-jail path (<see cref="SandboxIsolatedPaths" />), because under isolation a host path names nothing
    ///     the child can reach — the jail is not even present at its host name inside.
    ///     <para>
    ///         The isolated chain sets the same variables in its own fixed allow-list, and these are emitted after it,
    ///         so this is a deliberate restatement rather than an oversight. It is worth the duplication: the promise
    ///         that a script's <c>HOME</c> and <c>TMPDIR</c> are per-invocation, discarded on return and charged to
    ///         the disk ceiling is THIS tool's promise, made in its description to the model, and a tool should not
    ///         inherit the thing it promises from a launch chain it does not own. If a provider's view of the jail
    ///         ever disagrees with the contract, the disagreement surfaces here rather than as a script quietly
    ///         caching into somewhere unmetered.
    ///     </para>
    ///     <para>
    ///         What is NOT restated is the numeric-library thread pinning. Those variables are derived from
    ///         <see cref="SandboxCreateRequest.ThreadLimit" />, which the create request already carries, and naming
    ///         them twice would let the tool's environment and the sandbox's CPU ceiling drift apart in exactly the
    ///         situation the pinning exists to prevent.
    ///     </para>
    /// </summary>
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
        builder.Append(Cap(result.StandardOutput, result.StandardOutputTruncated));
        builder.Append("\nstderr:\n");
        builder.Append(Cap(result.StandardError, result.StandardErrorTruncated));
        return builder.ToString();
    }

    private string Cap(string value, bool providerTruncated)
    {
        if (Encoding.UTF8.GetByteCount(value) <= _options.MaxOutputBytes)
        {
            return providerTruncated ? value + Marker : value;
        }

        return TruncateToUtf8ByteBudget(value, _options.MaxOutputBytes) + Marker;
    }

    /// <summary>
    ///     Keeps the HEAD of a stream within a BYTE budget without ever splitting a rune, which is what a plain
    ///     <c>GetBytes</c>/<c>GetString</c> slice would do at a multi-byte boundary. Same algorithm as
    ///     <c>HostProcessExecutor.CappedOutput</c>'s, for the same reason: the cap is expressed in bytes and the value is
    ///     text.
    /// </summary>
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
