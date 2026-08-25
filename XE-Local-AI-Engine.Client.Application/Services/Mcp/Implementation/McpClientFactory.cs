namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Builds the transport for a registration and connects an <see cref="McpClient" />.
///     <para>
///         A stdio registration is routed by its <see cref="McpTrustTier" />: <see cref="McpTrustTier.Sandboxed" />
///         (the default) launches the server inside the substrate through
///         <see cref="SandboxedMcpStdioTransport" />, and <see cref="McpTrustTier.PrivilegedHost" /> keeps the plain
///         host launch this factory has always done — now as an explicit per-server operator grant rather than as the
///         only behaviour there is. See <c>docs/security/mcp-trust-tiers.md</c>.
///     </para>
///     <para>
///         The HTTP loopback check is defence in depth: the CRUD service validates loopback on register, but
///         re-validating here guarantees a row carrying a non-loopback URL can never cause an outbound connection to
///         an arbitrary remote server.
///     </para>
/// </summary>
internal sealed class McpClientFactory : IMcpClientFactory
{
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly INodeDataDirectory _nodeDataDirectory;
    private readonly McpOptions _options;
    private readonly IAgentSandboxRuntimeProvider _sandboxProvider;

    public McpClientFactory(IOptions<McpOptions> options,
        IAgentSandboxRuntimeProvider sandboxProvider,
        IAgentHomeIdentityProvider identityProvider,
        INodeDataDirectory nodeDataDirectory,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _sandboxProvider = sandboxProvider ?? throw new ArgumentNullException(nameof(sandboxProvider));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        // Only to know which host root a sandboxed server must never be able to read; nothing here writes to it.
        _nodeDataDirectory = nodeDataDirectory ?? throw new ArgumentNullException(nameof(nodeDataDirectory));
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
    ///     The tier decides WHERE the server's process runs, and it is the only place in this factory that decides it.
    ///     An unrecognized tier is refused rather than defaulted: a stored value nothing here understands must not be
    ///     resolved to the privileged branch by accident, and the schema check constraint means reaching this is a
    ///     code-versus-database mismatch worth surfacing.
    /// </summary>
    private IClientTransport BuildStdioTransport(McpServerRecord record)
    {
        return record.TrustTier switch
        {
            McpTrustTier.Sandboxed => new SandboxedMcpStdioTransport(record, _sandboxProvider, _identityProvider, _nodeDataDirectory, _loggerFactory),
            McpTrustTier.PrivilegedHost => new StdioClientTransport(BuildStdioTransportOptions(record), _loggerFactory),
            // BuiltInTrusted names an engine-owned transport and there is no engine-owned STDIO one. A row carrying it
            // reached the database past the CRUD refusal and the schema check, so it is a mismatch, not a tier to
            // serve — and serving it as either of the other two would be picking a privilege level on its behalf.
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

        // The PrivilegedHost launch path. (The sandboxed path does not come through here: the isolated chain clears
        // the environment inside the namespace and re-emits an allow-list plus the configured variables, so it applies
        // the same rule by a different mechanism.)
        //
        // Never let a stdio MCP server inherit the node's full process environment — it can hold secrets such as
        // XE_NODE_SQLITE_KEY when env-provisioned. ModelContextProtocol 1.4.0 defaults InheritEnvironmentVariables to
        // true; force it off and seed only the SDK's minimal default set (PATH/HOME/etc.), then overlay the per-server
        // configured variables on top. (Scope is the MCP transport only — the pinned native llama/sd launchers are
        // deliberately NOT scrubbed, because they are engine-authored launches of binaries this node installed rather
        // than operator-configured third-party executables. Do not "helpfully" extend this scrub to them.)
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
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
            // Re-validate the scheme at connect time (defence in depth, symmetric with the host check): a row that ever
            // reaches connect with ftp/file/etc. must not be handed to HttpClientTransport, even if it slipped past the
            // create-time validation (future code path, direct DB write, or a CRUD-layer regression).
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
