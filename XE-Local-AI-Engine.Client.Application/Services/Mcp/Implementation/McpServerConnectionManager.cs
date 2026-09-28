namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Owns the MCP client connections and keeps the MCP tool registry in sync with the enabled registrations.
/// </summary>
/// <remarks>
///     Each refresh reconciles live clients against the store's enabled set, discovers tools from newly connected
///     servers, qualifies and approval-wraps them, then republishes a deterministically ordered immutable snapshot
///     into the <see cref="IMcpToolRegistry" />. A per-server connect and list timeout plus per-server failure
///     isolation keep a hung or hostile server from stalling or aborting the refresh.
/// </remarks>
internal sealed class McpServerConnectionManager : IMcpServerConnectionManager, IAsyncDisposable
{
    private readonly IMcpClientFactory _clientFactory;
    private readonly Dictionary<Guid, ConnectedServer> _connections = [];
    private readonly ILogger<McpServerConnectionManager> _logger;
    private readonly int _maxInvalidToolCalls;
    private readonly int _maxToolResultCharacters;
    private readonly McpOptions _options;
    private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
    private readonly IMcpToolRegistry _registry;

    // Read only on a sandbox failure, to tell "this node cannot sandbox at all" from "the sandbox refused this server".
    private readonly IAgentSandboxRuntimeProvider _sandboxProvider;

    // The store is DbContext-backed and therefore Scoped, so this singleton manager resolves it per refresh through a scope rather
    // than capturing it: a captive dependency would fail ValidateOnBuild and risk concurrent DbContext use.
    private readonly IServiceScopeFactory _scopeFactory;

    // Guards _connections and _statuses (mutated only under the refresh gate, but GetStatuses reads concurrently).
    private readonly Lock _stateLock = new();

    private bool _disposed;
    private ImmutableArray<McpServerConnectionStatus> _statuses = [];

    public McpServerConnectionManager(IServiceScopeFactory scopeFactory,
        IMcpToolRegistry registry,
        IMcpClientFactory clientFactory,
        IAgentSandboxRuntimeProvider sandboxProvider,
        IOptions<McpOptions> options,
        IOptions<AgentToolPipelineOptions> pipelineOptions,
        ILogger<McpServerConnectionManager> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _sandboxProvider = sandboxProvider ?? throw new ArgumentNullException(nameof(sandboxProvider));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pipelineOptions);
        _options = options.Value;
        _maxToolResultCharacters = pipelineOptions.Value.MaxToolResultCharacters;
        _maxInvalidToolCalls = pipelineOptions.Value.MaxConsecutiveInvalidToolCallsPerTool;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var server in _connections.Values)
        {
            await DisposeClientSafelyAsync(server);
        }

        _connections.Clear();
        _refreshGate.Dispose();
    }

    public IReadOnlyList<McpServerConnectionStatus> GetStatuses()
    {
        lock (_stateLock)
        {
            return _statuses;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<McpServerRecord> enabled;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMcpServerStore>();
            enabled = await store.ListEnabledAsync(cancellationToken);
        }

        var enabledById = enabled.ToDictionary(static record => record.Id);

        // Assign a stable, unique slug per server for the qualified tool name, a numeric suffix disambiguating names that normalize
        // alike. Computed BEFORE the drop loop, so the keep-predicate sees a slug shift that would leave cached qualified names stale.
        var slugsByServer = AssignServerSlugs(enabled);

        // Drop clients no longer enabled, whose connection-affecting Version changed, or whose freshly computed slug differs from the
        // one their cached tool names were baked with. The diff runs over a copy of the keys, so _connections can be mutated meanwhile.
        foreach (var id in _connections.Keys.ToList())
        {
            var existing = _connections[id];
            var keep = enabledById.TryGetValue(id, out var record)
                       && record.Version == existing.Version
                       && string.Equals(slugsByServer[id], existing.Slug, StringComparison.Ordinal);
            if (!keep)
            {
                _ = _connections.Remove(id);
                await DisposeClientSafelyAsync(existing);
            }
        }

        var statuses = new List<McpServerConnectionStatus>(enabled.Count);
        foreach (var record in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slug = slugsByServer[record.Id];

            if (_connections.TryGetValue(record.Id, out var alreadyConnected))
            {
                // Still connected at the same Version: keep its tools, refresh its status entry.
                statuses.Add(BuildConnectedStatus(record, alreadyConnected));
                continue;
            }

            var (connected, error, reason) = await ConnectServerAsync(record, slug, cancellationToken);
            if (connected is not null)
            {
                _connections[record.Id] = connected;
                statuses.Add(BuildConnectedStatus(record, connected));
            }
            else
            {
                statuses.Add(new McpServerConnectionStatus
                {
                    ServerId = record.Id,
                    Name = record.Name,
                    Connected = false,
                    ToolCount = 0,
                    LastError = error,
                    FailureReason = reason,
                    Tools = []
                });
            }
        }

        PublishSnapshot();
        lock (_stateLock)
        {
            _statuses = [.. statuses];
        }
    }

    /// <summary>
    ///     Connects one server under a per-server timeout and lists its tools, returning the connected server with its
    ///     qualified, approval-wrapped tools, or a redacted error.
    /// </summary>
    /// <remarks>
    ///     Any specific failure is isolated, so it never aborts the refresh or the other servers.
    /// </remarks>
    private async Task<ConnectResult> ConnectServerAsync(McpServerRecord record, string slug, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

        McpClient? client = null;
        try
        {
            client = await _clientFactory.CreateAsync(record, timeoutCts.Token);
            var discovered = await client.ListToolsAsync(cancellationToken: timeoutCts.Token);

            var tools = BuildRegisteredTools(discovered, slug, ResolveToolCategory(record), _maxToolResultCharacters, _maxInvalidToolCalls, TimeSpan.FromSeconds(_options.ToolCallTimeoutSeconds));
            return new ConnectResult(new ConnectedServer
            {
                Client = client,
                Version = record.Version,
                Slug = slug,
                Tools = tools
            }, Error: null, Reason: null);
        }
        catch (SandboxCapabilityNotSupportedException ex)
        {
            // The reason still tells an operator "this node cannot sandbox" from "your server is broken", but the exception text
            // can name a sensitive host path (a denied root, the home directory), so it stays in the log like every other failure.
            var reason = _sandboxProvider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation)
                ? McpConnectionFailureReason.SandboxRefused
                : McpConnectionFailureReason.SandboxUnavailable;
            _logger.LogWarning(ex, "MCP server {ServerId} could not be started under its trust tier ({Reason}); it will contribute no tools.", record.Id, reason);
            await DisposePartialClientAsync(client, record.Version, slug);
            return new ConnectResult(Server: null, SafeMessage(reason), reason);
        }
        catch (Exception ex) when (ex is McpException
                                       or HttpRequestException
                                       or IOException
                                       or SocketException
                                       or TimeoutException
                                       or JsonException
                                       or NotSupportedException
                                       or AuthenticationException
                                       or InvalidOperationException
                                       or ArgumentException)
        {
            // A connect or list failure for one server must never abort the refresh or leave the others half-applied, so the catch
            // covers the realistic transport, timeout, schema, TLS and configuration set. Caller cancellation is NOT caught: it propagates.
            var reason = Classify(ex);
            _logger.LogWarning(ex, "MCP server {ServerId} failed to connect or list tools ({Reason}); it will contribute no tools.", record.Id, reason);
            await DisposePartialClientAsync(client, record.Version, slug);
            return new ConnectResult(Server: null, SafeMessage(reason), reason);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The per-server timeout fired (not a caller cancel). Treat it like any other isolated failure.
            _logger.LogWarning("MCP server {ServerId} timed out after {TimeoutSeconds}s; it will contribute no tools.", record.Id, _options.ConnectTimeoutSeconds);
            await DisposePartialClientAsync(client, record.Version, slug);
            return new ConnectResult(Server: null, SafeMessage(McpConnectionFailureReason.Timeout), McpConnectionFailureReason.Timeout);
        }
    }

    private async ValueTask DisposePartialClientAsync(McpClient? client, int version, string slug)
    {
        if (client is not null)
        {
            await DisposeClientSafelyAsync(new ConnectedServer
            {
                Client = client,
                Version = version,
                Slug = slug,
                Tools = []
            });
        }
    }

    /// <summary>
    ///     The risk class every tool from one server is offered under.
    /// </summary>
    /// <remarks>
    ///     <see cref="ToolCategory.Network" />, "reaches an external or out-of-process surface", is true of every MCP
    ///     tool and the whole story for a loopback HTTP server and a sandboxed stdio server, neither of which this node
    ///     grants host reach. A <see cref="McpTrustTier.PrivilegedHost" /> STDIO server is different in kind: this node
    ///     launches it as an unconfined child of its own user, so it is <see cref="ToolCategory.WriteExecute" />, which
    ///     a node policy can tighten and which keeps the catalog badge and every audit row truthful.
    /// </remarks>
    private static ToolCategory ResolveToolCategory(McpServerRecord record)
    {
        return record is { TransportKind: McpTransportKind.Stdio, TrustTier: McpTrustTier.PrivilegedHost }
            ? ToolCategory.WriteExecute
            : ToolCategory.Network;
    }

    /// <summary>
    ///     Renames each discovered tool to a collision-free qualified name (<c>mcp__{slug}__{tool}</c>), builds its
    ///     offer descriptor with approval on by default, and wraps the executable.
    /// </summary>
    /// <remarks>
    ///     The wrapping bounds the server round-trip with the per-call timeout, the result with the shared tool-result
    ///     budget, and the executable in an approval gate. Approval itself is unaffected by the risk class: every MCP
    ///     tool is approval-required and ineligible for a remembered session approval.
    /// </remarks>
    private static IReadOnlyList<McpRegisteredTool> BuildRegisteredTools(IList<McpClientTool> discovered, string slug, ToolCategory category, int maxToolResultCharacters, int maxInvalidToolCalls,
        TimeSpan toolCallTimeout)
    {
        var registered = new List<McpRegisteredTool>(discovered.Count);
        foreach (var tool in discovered)
        {
            var qualifiedName = $"mcp__{slug}__{tool.Name}";
            AIFunction named = tool.WithName(qualifiedName);

            // Every MCP tool defaults to requiring approval; the per-tool auto-execute opt-in lives in a bound agent
            // definition's ToolApprovals override, applied at projection — never in the catalog.
            const bool requiresApproval = true;
            var descriptor = new LocalChatToolDescriptor
            {
                Name = qualifiedName,
                Description = named.Description,
                ParameterSchema = named.JsonSchema.GetRawText(),
                RequiresApproval = requiresApproval,
                // Every MCP tool reaches an external/out-of-process server surface; a PrivilegedHost stdio server also
                // reaches this host's filesystem and shell. See ResolveToolCategory.
                Category = category
            };

            // Bound the server round-trip with the per-call timeout INNERMOST, below arg-repair and the result budget, so only the SDK
            // call is timed: a stall returns a typed tool-failure result and the run continues, never a retry.
            AIFunction timed = new McpToolCallTimeoutAIFunction(named, toolCallTimeout);

            // Validate the model's arguments against the tool's schema and run the repair loop before the server sees them, the guard
            // the ClientLocal registry applies. Unknown-property rejection is OFF: an under-declared server schema must not bounce a needed key.
            AIFunction validated = new ToolArgumentRepairAIFunction(timed, maxInvalidToolCalls, rejectUnknownProperties: false);

            // Backstop the (verbatim) MCP result with the shared budget UNDER the approval gate, so a server can't
            // flood chat history and ApprovalRequiredAIFunction stays the outermost type the approval pipeline detects.
            AIFunction budgeted = new BudgetedToolResultAIFunction(validated, maxToolResultCharacters);
            AITool executable = new ApprovalRequiredAIFunction(budgeted);
            registered.Add(new McpRegisteredTool
            {
                Name = qualifiedName,
                Executable = executable,
                Descriptor = descriptor
            });
        }

        return registered;
    }

    /// <summary>
    ///     Builds the full, deterministically ordered tool list from every connected server and republishes it to the
    ///     registry in one atomic snapshot swap.
    /// </summary>
    private void PublishSnapshot()
    {
        var all = _connections.Values
                              .SelectMany(static server => server.Tools)
                              .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                              .ToList();

        _registry.ReplaceSnapshot(all);
    }

    private static McpServerConnectionStatus BuildConnectedStatus(McpServerRecord record, ConnectedServer server)
    {
        var tools = server.Tools
                          .Select(static tool => new McpServerToolInfo
                          {
                              Name = tool.Name,
                              Description = tool.Descriptor.Description,
                              RequiresApproval = tool.Descriptor.RequiresApproval
                          })
                          .ToList();

        return new McpServerConnectionStatus
        {
            ServerId = record.Id,
            Name = record.Name,
            Connected = true,
            ToolCount = tools.Count,
            LastError = null,
            Tools = tools
        };
    }

    /// <summary>
    ///     Assigns a unique kebab slug to each server, derived from its Name.
    /// </summary>
    /// <remarks>
    ///     The store enforces a unique Name, but two distinct names can normalize to the same slug, so a numeric suffix
    ///     disambiguates collisions deterministically, servers being processed oldest first as the store orders them.
    /// </remarks>
    private static Dictionary<Guid, string> AssignServerSlugs(IReadOnlyList<McpServerRecord> servers)
    {
        var slugs = new Dictionary<Guid, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            var baseSlug = Slugify(server.Name);
            var slug = baseSlug;
            var suffix = 2;
            while (!used.Add(slug))
            {
                slug = string.Create(CultureInfo.InvariantCulture, $"{baseSlug}-{suffix}");
                suffix++;
            }

            slugs[server.Id] = slug;
        }

        return slugs;
    }

    /// <summary>
    ///     Normalizes a server name to a lowercase kebab slug: ASCII letters and digits pass through lowercased, every
    ///     other run collapses to a single hyphen, and leading and trailing hyphens are trimmed.
    /// </summary>
    /// <remarks>
    ///     An empty result falls back to <c>server</c>, so the qualified name is always well-formed.
    /// </remarks>
    private static string Slugify(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastWasHyphen = false;
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                _ = builder.Append(char.ToLowerInvariant(ch));
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                _ = builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "server" : slug;
    }

    /// <summary>
    ///     Maps a caught connect or list failure to the reason the management panel words.
    /// </summary>
    /// <remarks>
    ///     A missing command surfaces two ways: the SDK's stdio launch wraps the failed <c>Process.Start</c> (a
    ///     <see cref="Win32Exception" />) in an <see cref="IOException" />, and the Sandboxed transport, whose launch
    ///     would only fail inside the jail, checks the command up front and throws <see cref="FileNotFoundException" />.
    ///     Order matters: both are <see cref="IOException" />s, as is the generic transport failure below them.
    /// </remarks>
    private static McpConnectionFailureReason Classify(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException or IOException { InnerException: Win32Exception } => McpConnectionFailureReason.ServerNotFound,
            AuthenticationException or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => McpConnectionFailureReason.Authentication,
            TimeoutException => McpConnectionFailureReason.Timeout,
            HttpRequestException or IOException or SocketException => McpConnectionFailureReason.Transport,
            McpException or JsonException => McpConnectionFailureReason.Protocol,
            _ => McpConnectionFailureReason.Unknown
        };
    }

    private static string SafeMessage(McpConnectionFailureReason reason)
    {
        // Connection, transport and sandbox messages can echo a command path, URL or protected host path, so the exception text
        // never reaches the UI: each reason has one fixed wording, and the real exception is logged server-side.
        return reason switch
        {
            McpConnectionFailureReason.SandboxUnavailable => "This node cannot isolate the MCP server from the host filesystem. Install bubblewrap (bwrap) with user-namespace support, or move the server to the Privileged host tier.",
            McpConnectionFailureReason.SandboxRefused => "The sandbox refused to start the MCP server: its command or working directory overlaps a protected location, or the sandbox boundary could not be established. Point it at the directory holding the server's own files.",
            McpConnectionFailureReason.ServerNotFound => "The MCP server's command was not found or could not be started.",
            McpConnectionFailureReason.Authentication => "The MCP server rejected the connection's credentials or its TLS negotiation failed.",
            McpConnectionFailureReason.Timeout => "Timed out connecting to the MCP server.",
            McpConnectionFailureReason.Transport => "The connection to the MCP server failed or closed.",
            McpConnectionFailureReason.Protocol => "The MCP server did not complete the MCP handshake.",
            _ => "The MCP server connection failed."
        };
    }

    private async ValueTask DisposeClientSafelyAsync(ConnectedServer server)
    {
        try
        {
            await server.Client.DisposeAsync();
        }
        catch (Exception ex) when (ex is McpException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Ignored error while disposing an MCP client during reconcile.");
        }
    }

    private sealed record ConnectedServer
    {
        public required McpClient Client { get; init; }

        public required int Version { get; init; }

        public required string Slug { get; init; }

        public required IReadOnlyList<McpRegisteredTool> Tools { get; init; }
    }

    private sealed record ConnectResult(ConnectedServer? Server, string? Error, McpConnectionFailureReason? Reason);
}
