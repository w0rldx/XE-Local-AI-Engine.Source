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
///         Egress denial and the resource ceilings are CAPABILITY-GATED rather than unconditional, exactly as AgentHome
///         requests them: the provider fails closed on a guarantee it cannot honor, so asking for containment a host
///         cannot deliver would not harden the tool — it would stop it running at all, with the degradation already
///         visible in the sandbox containment log rather than silent. What the sandbox does NOT provide is a filesystem
///         boundary (this provider has no mount layer, so the child sees the host filesystem as the worker user); that
///         is why the tool is <c>WriteExecute</c>, approval-required, off by default, and never offered to a
///         cloud-hosted model.
///     </para>
/// </remarks>
internal sealed class ComputeToolGateway : IComputeToolGateway
{
    /// <summary>The sandbox runtime profile this tool's jail is keyed on — deliberately not AgentHome's.</summary>
    internal const string RuntimeProfile = "compute-python";

    /// <summary>
    ///     The attach-key generation for the compute jail. It is its OWN constant rather than AgentHome's manifest
    ///     version because this jail has no manifest: borrowing that number would make an AgentHome layout change
    ///     silently re-key a sandbox that shares nothing with it. Bump this only to force a fresh compute jail.
    /// </summary>
    private const int SandboxGeneration = 1;

    /// <summary>The marker every capped stream in this product ends with, so a model reads one convention.</summary>
    private const string Marker = "…[output truncated]";

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

        try
        {
            var interpreter = await _environment.GetInterpreterPathAsync(cancellationToken).ConfigureAwait(false);
            var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            var handle = await _provider.CreateOrAttachAsync(BuildCreateRequest(identity), cancellationToken).ConfigureAwait(false);
            var result = await _provider.ExecuteAsync(handle, BuildCommandRequest(interpreter, code), cancellationToken)
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
    }

    private SandboxCreateRequest BuildCreateRequest(AgentHomeOwnerIdentity identity)
    {
        var capabilities = _provider.Capabilities;
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = identity.OwnerUserId,
                NodeId = identity.NodeId,
                ProviderName = _provider.ProviderName,
                RuntimeProfile = RuntimeProfile,
                ManifestVersion = SandboxGeneration
            },
            RuntimeProfile = RuntimeProfile,
            NetworkPolicy = capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy)
                ? SandboxNetworkPolicy.None
                : SandboxNetworkPolicy.Unrestricted,
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

    private SandboxCommandRequest BuildCommandRequest(string interpreter, string code)
    {
        return new SandboxCommandRequest
        {
            ExecutionId = "compute-" + Guid.NewGuid().ToString("N"),
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
    ///     The environment layered over the provider's own allow-list. HOME and TMPDIR are pointed at a compute-owned
    ///     scratch directory rather than left inherited: the provider identity-maps the filesystem, so an inherited HOME
    ///     would let a script read and write the operator's <c>~/.config</c> and <c>~/.cache</c> as an ordinary side
    ///     effect of importing a library, which nothing about running arithmetic needs.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironment()
    {
        var scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine",
            "compute-runtime",
            "scratch");
        Directory.CreateDirectory(scratch);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = scratch,
            ["TMPDIR"] = scratch,
            ["TMP"] = scratch,
            ["TEMP"] = scratch,
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
