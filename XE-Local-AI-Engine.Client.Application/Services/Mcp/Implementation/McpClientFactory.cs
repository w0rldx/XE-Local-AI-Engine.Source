namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Builds the transport for a registration (stdio process or loopback-validated HTTP/SSE endpoint) and connects an
///     <see cref="McpClient" />. The HTTP loopback check is defence in depth: the CRUD service validates loopback on
///     register, but re-validating here guarantees a row carrying a non-loopback URL can never cause an outbound
///     connection to an arbitrary remote server.
/// </summary>
internal sealed class McpClientFactory : IMcpClientFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly McpOptions _options;

    public McpClientFactory(IOptions<McpOptions> options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public Task<McpClient> CreateAsync(McpServerRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var transport = BuildTransport(record);
        return McpClient.CreateAsync(transport, clientOptions: null, _loggerFactory, cancellationToken);
    }

    private IClientTransport BuildTransport(McpServerRecord record)
    {
        return record.TransportKind switch
        {
            McpTransportKind.Stdio => BuildStdioTransport(record),
            McpTransportKind.Http => BuildHttpTransport(record),
            _ => throw new InvalidOperationException($"Unsupported MCP transport kind '{record.TransportKind}'.")
        };
    }

    private IClientTransport BuildStdioTransport(McpServerRecord record)
    {
        return new StdioClientTransport(BuildStdioTransportOptions(record), _loggerFactory);
    }

    // Internal for the transport-hardening test (GPTAUD-22): asserts the built options never inherit the parent env.
    internal static StdioClientTransportOptions BuildStdioTransportOptions(McpServerRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Command))
        {
            throw new InvalidOperationException("A stdio MCP server requires a command.");
        }

        // Never let a stdio MCP server inherit the node's full process environment — it can hold secrets such as
        // XE_NODE_SQLITE_KEY when env-provisioned. ModelContextProtocol 1.4.0 defaults InheritEnvironmentVariables to
        // true; force it off and seed only the SDK's minimal default set (PATH/HOME/etc.), then overlay the per-server
        // configured variables on top. (Scope is the MCP transport only; the pinned native llama/sd launchers are
        // deliberately NOT scrubbed here, per docs/agent-knowledge §runtime.)
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
