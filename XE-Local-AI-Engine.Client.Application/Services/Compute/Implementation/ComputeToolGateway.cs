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
///         Egress denial is UNCONDITIONAL and fails the call closed: "no network" is what the tool's description
///         promises the model and what the user approved the call on, so a host that cannot build an empty network
///         namespace gets a refusal rather than a script with the operator's network. The resource ceilings stay
///         capability-gated, because they bound cost rather than reachability — degrading them is visible in the
///         containment log and costs no guarantee.
///     </para>
///     <para>
///         What the sandbox does NOT provide is a filesystem boundary: this provider has no mount layer and the child
///         runs under the SAME uid as the engine (<c>unshare --user --map-current-user</c>), so a script sees, and can
///         write, everything that user can. The venv is chmod'd read-only after provisioning, which stops accidental
///         and low-effort writes into <c>site-packages</c>, but a deliberate script can chmod it back — see
///         <c>ComputePythonEnvironment</c>. That is why the tool is <c>WriteExecute</c>, approval-required, off by
///         default, and never offered to a cloud-hosted model.
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
    ///     The script's <c>HOME</c>, as a sandbox-relative path under the jail root. Below the jail deliberately: the
    ///     provider's disk watchdog meters the JAIL, so a scratch directory anywhere else is unmetered space a script
    ///     can fill while the ceiling it was given reports nothing.
    /// </summary>
    private const string HomeDirectoryName = "home";

    /// <summary>The script's <c>TMPDIR</c>/<c>TMP</c>/<c>TEMP</c>, likewise under the jail root and likewise metered.</summary>
    private const string TempDirectoryName = ".tmp";

    private readonly IComputePythonEnvironment _environment;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILogger<ComputeToolGateway> _logger;
    private readonly ComputeOptions _options;
    private readonly IAgentSandboxRuntimeProvider _provider;

    public ComputeToolGateway(IAgentSandboxRuntimeProvider provider,
        IAgentHomeIdentityProvider identityProvider,
        IComputePythonEnvironment environment,
        IOptions<ComputeOptions> options,
        ILogger<ComputeToolGateway> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
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

        // Offline is ADVERTISED, so it fails closed. Asking for Unrestricted when the host cannot build an empty
        // network namespace silently handed a model-authored script the operator's network — the one guarantee the
        // tool's description makes and the user approved the call on. Refusing is the honest answer; the resource
        // ceilings below stay capability-gated because they bound cost, not reachability.
        if (!_provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy))
        {
            _logger.LogWarning(
                "run_python refused: the '{Provider}' sandbox provider cannot deny egress on this host, and the tool's offline guarantee is not optional. Install the user-namespace support the sandbox containment probe reports as missing, or leave Compute:Enabled off.",
                _provider.ProviderName);
            return "run_python rejected: this node cannot run the compute sandbox offline, and the tool never runs with network access.";
        }

        // One id for everything this invocation owns: its jail and its execution. Both are per call, and killing the
        // jail discards every byte the script wrote — including its scratch, which lives inside it. That is what makes
        // the advertised statelessness real rather than a claim. The provisioned venv is NOT here: it lives under the
        // compute cache root, costs seconds to build, and is read-only to the script, so it survives untouched.
        var invocationId = Guid.NewGuid().ToString("N");
        SandboxHandle? handle = null;
        try
        {
            var interpreter = await _environment.GetInterpreterPathAsync(cancellationToken).ConfigureAwait(false);
            var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            handle = await _provider.CreateOrAttachAsync(BuildCreateRequest(identity, invocationId), cancellationToken).ConfigureAwait(false);
            if (handle.WorkingRoot is not { } jailRoot)
            {
                // Fails closed for the same reason the egress check does: the alternative is pointing HOME and TMPDIR
                // somewhere the disk ceiling this call was granted does not measure, which reads as a bounded sandbox
                // and is not one. No agent-role provider that actually executes anything hits this.
                _logger.LogWarning(
                    "run_python refused: the '{Provider}' sandbox provider reports no jail root, so the script's HOME/TMPDIR scratch cannot be placed inside the directory the disk ceiling meters.",
                    _provider.ProviderName);
                return "run_python rejected: this node's sandbox cannot give the script a scratch directory inside its own jail.";
            }

            var scratch = await CreateScratchAsync(handle, jailRoot, cancellationToken).ConfigureAwait(false);
            var result = await _provider.ExecuteAsync(handle, BuildCommandRequest(interpreter, code, scratch, invocationId), cancellationToken)
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
    ///     Materializes the script's HOME and TMPDIR inside the jail, through the provider's own reset operation rather
    ///     than a host <c>Directory.CreateDirectory</c>: that is the surface that applies the jail's path and symlink
    ///     guards, and it is the only one that stays correct for a provider whose sandbox paths are not host paths.
    ///     <para>
    ///         Returns the paths as the CHILD sees them, which is what the environment must carry. For the process
    ///         provider — the only agent-role provider that runs a real process — the jail is identity-mapped, so those
    ///         are the same bytes the watchdog walks.
    ///     </para>
    /// </summary>
    private async Task<ComputeScratchPaths> CreateScratchAsync(SandboxHandle handle, string jailRoot, CancellationToken cancellationToken)
    {
        await _provider.ResetDirectoryAsync(handle, HomeDirectoryName, cancellationToken).ConfigureAwait(false);
        await _provider.ResetDirectoryAsync(handle, TempDirectoryName, cancellationToken).ConfigureAwait(false);
        return new ComputeScratchPaths(Path.Combine(jailRoot, HomeDirectoryName), Path.Combine(jailRoot, TempDirectoryName));
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
    private SandboxCreateRequest BuildCreateRequest(AgentHomeOwnerIdentity identity, string invocationId)
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
            // Unconditional: ExecuteAsync already refused the call if this provider cannot honor it.
            NetworkPolicy = SandboxNetworkPolicy.None,
            // Unconditional too, and for the same reason it needs no capability check: it can only ASK FOR LESS than
            // the node-wide jail ceiling the provider would otherwise apply, so a provider that ignores it is no worse
            // off than before. A script doing arithmetic writes almost nothing, and inheriting the node-wide number let
            // one runaway `open(..., "w")` loop consume the whole allowance a workspace run is sized for.
            MaxJailDiskBytes = _options.MaxJailDiskBytes,
            ResourceLimits = capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits)
                ? new SandboxResourceLimits
                {
                    CpuCount = _options.CpuCount,
                    MemoryMb = _options.MemoryMb,
                    PidsLimit = _options.PidsLimit
                }
                : null
        };
    }

    private SandboxCommandRequest BuildCommandRequest(string interpreter, string code, ComputeScratchPaths scratch, string invocationId)
    {
        return new SandboxCommandRequest
        {
            ExecutionId = "compute-" + invocationId,
            Executable = interpreter,
            // `-I` is isolated mode: no PYTHONPATH, no user site-packages, no script-directory import. It is what keeps
            // the interpreter's import surface the provisioned lockfile closure rather than whatever happens to sit in
            // the working directory or the operator's environment. `-` reads the program from stdin, so the script is
            // never written to disk and never becomes an argv the process table exposes.
            Arguments = ["-I", "-"],
            StandardInput = code,
            Environment = BuildEnvironment(scratch),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
    }

    /// <summary>
    ///     The environment layered over the provider's own allow-list. HOME and TMPDIR are pointed at a compute-owned
    ///     scratch directory rather than left inherited: the provider identity-maps the filesystem, so an inherited HOME
    ///     would let a script read and write the operator's <c>~/.config</c> and <c>~/.cache</c> as an ordinary side
    ///     effect of importing a library, which nothing about running arithmetic needs.
    ///     <para>
    ///         The directories are per INVOCATION and live INSIDE that invocation's jail. Both halves are load-bearing.
    ///         Per invocation, because a shared scratch made every cache, dotfile and temp file a script writes
    ///         readable by the next call — including a call in another conversation. Inside the jail, because the disk
    ///         ceiling this call was granted is enforced by walking the jail: while the scratch sat beside the venv
    ///         under the node data directory, a script could fill the host disk through <c>TMPDIR</c> and the watchdog
    ///         measured nothing. They now cost the same budget as anything else the script writes, and the jail
    ///         teardown reclaims them.
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironment(ComputeScratchPaths scratch)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = scratch.Home,
            ["TMPDIR"] = scratch.Temp,
            ["TMP"] = scratch.Temp,
            ["TEMP"] = scratch.Temp,
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

    /// <summary>
    ///     The two writable scratch paths one invocation hands its script, as the child sees them. Separate directories
    ///     rather than one: <c>HOME</c> is where a library drops a dotfile or a cache it may re-read within the call,
    ///     and <c>TMPDIR</c> is where it drops files it expects to be able to clear — a single directory conflated the
    ///     two and made a script's own <c>tempfile</c> cleanup look like it had wiped its home.
    /// </summary>
    private readonly record struct ComputeScratchPaths(string Home, string Temp);
}
