namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Builds the transport for a registration and connects an <see cref="McpClient" />.
/// </summary>
/// <remarks>
///     A stdio registration is routed by its <see cref="McpTrustTier" />: <see cref="McpTrustTier.Sandboxed" />, the
///     default, launches the server inside the substrate through <see cref="SandboxedMcpStdioTransport" />, while
///     <see cref="McpTrustTier.PrivilegedHost" /> is the plain host launch, an explicit per-server operator grant. See
///     <c>docs/security/mcp-trust-tiers.md</c>. The HTTP loopback check here is defence in depth: the CRUD service
///     validates on register, and re-validating guarantees a non-loopback row can never reach a remote server.
/// </remarks>
internal sealed class McpClientFactory : IMcpClientFactory
{
    private readonly IOptions<ComputeOptions> _ceilingDefaults;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly INodeDataDirectory _nodeDataDirectory;
    private readonly IOptions<LocalContainerOptions> _nodeOptions;
    private readonly McpOptions _options;
    private readonly IAgentSandboxRuntimeProvider _sandboxProvider;

    public McpClientFactory(IOptions<McpOptions> options,
        IAgentSandboxRuntimeProvider sandboxProvider,
        IAgentHomeIdentityProvider identityProvider,
        INodeDataDirectory nodeDataDirectory,
        IOptions<ComputeOptions> ceilingDefaults,
        IOptions<LocalContainerOptions> nodeOptions,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _sandboxProvider = sandboxProvider ?? throw new ArgumentNullException(nameof(sandboxProvider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        // Only to know which host root a sandboxed server must never be able to read; nothing here writes to it.
        _nodeDataDirectory = nodeDataDirectory ?? throw new ArgumentNullException(nameof(nodeDataDirectory));
        _ceilingDefaults = ceilingDefaults ?? throw new ArgumentNullException(nameof(ceilingDefaults));
        _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public Task<McpClient> CreateAsync(McpServerRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var transport = BuildTransport(record);
        return McpClient.CreateAsync(transport, clientOptions: null, _loggerFactory, cancellationToken);
    }

    // Internal for the tier-routing test: which TRANSPORT TYPE a record resolves to is the whole of the "where does
    // this process run" decision, and asserting it needs no process, no sandbox and no host capability.
    internal IClientTransport BuildTransport(McpServerRecord record)
    {
        return record.TransportKind switch
        {
            McpTransportKind.Stdio => BuildStdioTransport(record),
            McpTransportKind.Http => BuildHttpTransport(record),
            _ => throw new InvalidOperationException($"Unsupported MCP transport kind '{record.TransportKind}'.")
        };
    }

    /// <summary>
    ///     The tier decides WHERE the server's process runs, and this is the only place in the factory that decides it.
    /// </summary>
    /// <remarks>
    ///     An unrecognized tier is refused rather than defaulted: a stored value nothing here understands must not
    ///     resolve to the privileged branch by accident, and the schema check constraint means reaching it at all is a
    ///     code-versus-database mismatch worth surfacing.
    /// </remarks>
    private IClientTransport BuildStdioTransport(McpServerRecord record)
    {
        return record.TrustTier switch
        {
            McpTrustTier.Sandboxed => new SandboxedMcpStdioTransport(record, _sandboxProvider, _identityProvider, _nodeDataDirectory, _ceilingDefaults, _nodeOptions, _loggerFactory),
            McpTrustTier.PrivilegedHost => new StdioClientTransport(BuildStdioTransportOptions(record), _loggerFactory),
            // BuiltInTrusted names an engine-owned transport and there is no engine-owned STDIO one: a row carrying it passed both
            // the CRUD refusal and the schema check, so serving it as either other tier would pick a privilege level on its behalf.
            _ => throw new InvalidOperationException($"Unsupported MCP trust tier '{record.TrustTier}' for a stdio server.")
        };
    }

    // Internal for the transport-hardening test: asserts the built options never inherit the parent env.
    internal static StdioClientTransportOptions BuildStdioTransportOptions(McpServerRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Command))
        {
            throw new InvalidOperationException("A stdio MCP server requires a command.");
        }

        // The PrivilegedHost launch never lets a stdio MCP server inherit the node's full process environment, which can hold secrets
        // such as XE_NODE_SQLITE_KEY: ModelContextProtocol 1.4.0 inherits by default, so it is forced off and only the SDK's minimal set seeded.
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        // The per-server configured variables overlay that set, as the sandboxed path re-emits them inside its cleared namespace. Scope is
        // the MCP transport: never extend this scrub to the engine's own pinned llama and sd launchers, which run binaries this node installed.
        foreach (var pair in record.Environment)
        {
            environment[pair.Key] = pair.Value;
        }

        return new StdioClientTransportOptions
        {
            Name = record.Name,
            Command = record.Command,
            Arguments = [.. record.Arguments],
            WorkingDirectory = string.IsNullOrWhiteSpace(record.WorkingDirectory) ? null : record.WorkingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment
        };
    }

    private IClientTransport BuildHttpTransport(McpServerRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Url) || !Uri.TryCreate(record.Url, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("An HTTP MCP server requires an absolute URL.");
        }

        if (!IsHttpScheme(endpoint.Scheme))
        {
            // Re-validate the scheme at connect time, defence in depth symmetric with the host check: a row reaching connect with
            // ftp or file must not be handed to HttpClientTransport, even if a direct DB write or CRUD regression let it through.
            throw new InvalidOperationException("An HTTP MCP server URL must use the http or https scheme.");
        }

        if (!IsLoopbackHost(endpoint.Host))
        {
            throw new InvalidOperationException("An HTTP MCP server URL must target a loopback host.");
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Name = record.Name,
            Endpoint = endpoint
        };

        return new HttpClientTransport(transportOptions, _loggerFactory);
    }

    private bool IsLoopbackHost(string host)
    {
        // Uri.Host returns an IPv6 literal WITH brackets (e.g. "[::1]"), but the allowlist stores the bare address
        // ("::1"); strip the brackets so a valid IPv6 loopback the front-end accepts is not rejected here.
        var normalizedHost = host.Trim('[', ']');
        return _options.HttpLoopbackHosts.Any(allowed => string.Equals(allowed, normalizedHost, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHttpScheme(string scheme)
    {
        return string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
