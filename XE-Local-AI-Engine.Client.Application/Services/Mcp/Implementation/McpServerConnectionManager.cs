namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.Collections.Immutable;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Owns the MCP client connections and keeps the MCP tool registry in sync with the enabled registrations. Each
///     refresh reconciles live clients against the store's enabled set, discovers tools from newly connected servers,
///     qualifies + approval-wraps them, and republishes a deterministically ordered immutable snapshot into the
///     <see cref="IMcpToolRegistry" />. A per-server connect/list timeout plus per-server failure isolation keep a hung
///     or hostile server from stalling or aborting the refresh.
/// </summary>
internal sealed class McpServerConnectionManager : IMcpServerConnectionManager, IAsyncDisposable
{
    private readonly IMcpClientFactory _clientFactory;
    private readonly Dictionary<Guid, ConnectedServer> _connections = [];
    private readonly ILogger<McpServerConnectionManager> _logger;
    private readonly McpOptions _options;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IMcpToolRegistry _registry;

    // The store is DbContext-backed and therefore Scoped, so a singleton manager must resolve it per refresh through a
    // scope rather than capturing it (a captive dependency would fail ValidateOnBuild and risk concurrent DbContext use).
    // This mirrors NodeChatPersistenceWriter / AgentHomeService.
    private readonly IServiceScopeFactory _scopeFactory;

    // Guards _connections and _statuses (mutated only under the refresh gate, but GetStatuses reads concurrently).
    private readonly object _stateLock = new();

    private bool _disposed;
    private ImmutableArray<McpServerConnectionStatus> _statuses = [];

    public McpServerConnectionManager(IServiceScopeFactory scopeFactory,
        IMcpToolRegistry registry,
        IMcpClientFactory clientFactory,
        IOptions<McpOptions> options,
        ILogger<McpServerConnectionManager> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
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
            await DisposeClientSafelyAsync(server).ConfigureAwait(false);
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

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
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
            enabled = await store.ListEnabledAsync(cancellationToken).ConfigureAwait(false);
        }

        var enabledById = enabled.ToDictionary(static record => record.Id);

        // Assign a stable, unique slug per server (used for the qualified tool name). Slugs are derived from Name; a
        // collision after normalization is disambiguated with a numeric suffix so two servers never share a namespace.
        // Computed BEFORE the drop loop so the keep-predicate can detect a slug shift (a colliding server changing an
        // existing server's disambiguation suffix), which would otherwise leave cached qualified names stale.
        var slugsByServer = AssignServerSlugs(enabled);

        // Drop clients that are no longer enabled, whose connection-affecting Version changed, or whose freshly-computed
        // slug differs from the one their cached tool names were baked with. The diff is computed against a copy of the
        // keys so we can mutate _connections while iterating.
        foreach (var id in _connections.Keys.ToList())
        {
            var existing = _connections[id];
            var keep = enabledById.TryGetValue(id, out var record)
                       && record.Version == existing.Version
                       && string.Equals(slugsByServer[id], existing.Slug, StringComparison.Ordinal);
            if (!keep)
            {
                _ = _connections.Remove(id);
                await DisposeClientSafelyAsync(existing).ConfigureAwait(false);
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

            var (connected, error) = await ConnectServerAsync(record, slug, cancellationToken).ConfigureAwait(false);
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
    ///     Connects one server under a per-server timeout and lists its tools. Returns the connected server (client +
    ///     discovered, qualified, approval-wrapped tools) on success, or a redacted error on any specific failure — the
    ///     failure is isolated so it never aborts the refresh or the other servers.
    /// </summary>
    private async Task<ConnectResult> ConnectServerAsync(McpServerRecord record, string slug, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

        McpClient? client = null;
        try
        {
            client = await _clientFactory.CreateAsync(record, timeoutCts.Token).ConfigureAwait(false);
            var discovered = await client.ListToolsAsync(cancellationToken: timeoutCts.Token).ConfigureAwait(false);

            var tools = BuildRegisteredTools(discovered, slug);
            return new ConnectResult(new ConnectedServer(client, record.Version, slug, tools), Error: null);
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
            // A connect/list failure for one server must never abort the refresh or leave the others half-applied.
            // The catch covers the realistic transport/protocol set: MCP/HTTP/socket/IO errors, a per-server timeout
            // (TimeoutException), a malformed tool schema (JsonException/NotSupportedException), a TLS/auth failure,
            // and a malformed transport configuration (ArgumentException/InvalidOperationException). Caller cancellation
            // (OperationCanceledException without the per-server timeout) is intentionally NOT caught here so it
            // propagates out of the refresh.
            _logger.LogWarning(ex, "MCP server {ServerId} failed to connect or list tools; it will contribute no tools.", record.Id);
            await DisposePartialClientAsync(client, record.Version, slug).ConfigureAwait(false);
            return new ConnectResult(Server: null, Redact(ex.Message));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The per-server timeout fired (not a caller cancel). Treat it like any other isolated failure.
            _logger.LogWarning("MCP server {ServerId} timed out after {TimeoutSeconds}s; it will contribute no tools.", record.Id, _options.ConnectTimeoutSeconds);
            await DisposePartialClientAsync(client, record.Version, slug).ConfigureAwait(false);
            return new ConnectResult(Server: null, "Timed out connecting to the MCP server.");
        }
    }

    private async ValueTask DisposePartialClientAsync(McpClient? client, int version, string slug)
    {
        if (client is not null)
        {
            await DisposeClientSafelyAsync(new ConnectedServer(client, version, slug, [])).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Renames each discovered tool to a collision-free qualified name (<c>mcp__{slug}__{tool}</c>), builds its
    ///     offer descriptor (approval ON by default), and wraps the executable in an approval gate.
    /// </summary>
    private static IReadOnlyList<McpRegisteredTool> BuildRegisteredTools(IList<McpClientTool> discovered, string slug)
    {
        var registered = new List<McpRegisteredTool>(discovered.Count);
        foreach (var tool in discovered)
        {
            var qualifiedName = $"mcp__{slug}__{tool.Name}";
            var named = tool.WithName(qualifiedName);

            // Every MCP tool defaults to requiring approval; the per-tool auto-execute opt-in lives in a bound agent
            // definition's ToolApprovals override, applied at projection — never in the catalog.
            const bool requiresApproval = true;
            var descriptor = new LocalChatToolDescriptor(qualifiedName,
                named.Description,
                named.JsonSchema.GetRawText(),
                requiresApproval);

            AITool executable = new ApprovalRequiredAIFunction(named);
            registered.Add(new McpRegisteredTool(qualifiedName, executable, descriptor));
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
    ///     Assigns a unique kebab slug to each server, derived from its Name. The store enforces a unique Name, but two
    ///     distinct names can normalize to the same slug, so a numeric suffix disambiguates collisions deterministically
    ///     (servers are processed oldest first, matching the store's ordering).
    /// </summary>
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
    ///     Normalizes a server name to a lowercase kebab slug: ASCII letters/digits pass through lowercased, every other
    ///     run collapses to a single hyphen, and leading/trailing hyphens are trimmed. An empty result falls back to
    ///     <c>server</c> so the qualified name is always well-formed.
    /// </summary>
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

    private static string Redact(string message)
    {
        // Connection/transport messages can echo a command path or URL. Keep the failure observable but never surface
        // a host path or secret to the UI: clamp to a short, generic reason.
        _ = message;
        return "The MCP server connection failed.";
    }

    private async ValueTask DisposeClientSafelyAsync(ConnectedServer server)
    {
        try
        {
            await server.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is McpException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Ignored error while disposing an MCP client during reconcile.");
        }
    }

    private sealed record ConnectedServer(McpClient Client, int Version, string Slug, IReadOnlyList<McpRegisteredTool> Tools);

    private sealed record ConnectResult(ConnectedServer? Server, string? Error);
}
