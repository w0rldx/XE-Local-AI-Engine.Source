namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.Collections.Concurrent;
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
using ModelContextProtocol.Protocol;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Owns the MCP client sessions and keeps the MCP tool registry in sync with the enabled registrations.
/// </summary>
/// <remarks>
///     A refresh persists each enabled server's slug on first sight, connects a shared session, lists the tools and
///     republishes an ordered snapshot into the <see cref="IMcpToolRegistry" />. Every executable calls through
///     <see cref="InvokeRoutedAsync" />, which picks the live session at call time, reconnects once when it has ended,
///     and otherwise fails typed; a dead server's tools stay offered. Per-server timeouts isolate a hung server.
/// </remarks>
internal sealed class McpServerConnectionManager : IMcpServerConnectionManager, IAsyncDisposable
{
    /// <summary>Per-conversation sessions a stdio server may run at once; the next conversation fails typed.</summary>
    internal const int MaxConversationSessionsPerStdioServer = 4;

    // Stderr can hold anything the server printed, so only a short, secret-scrubbed tail reaches the UI.
    private const int MaxStderrTailCharacters = 1024;

    /// <summary>A per-conversation session unused this long is disposed.</summary>
    internal static readonly TimeSpan ConversationSessionIdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A burst of <c>tools/list_changed</c> notifications re-lists once, this long after the last one.</summary>
    internal static readonly TimeSpan ToolListChangedDebounce = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(1);

    private readonly IMcpClientFactory _clientFactory;
    private readonly ITimer _idleSweepTimer;
    private readonly ILogger<McpServerConnectionManager> _logger;
    private readonly int _maxInvalidToolCalls;
    private readonly int _maxToolResultCharacters;
    private readonly McpOptions _options;
    private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
    private readonly IMcpToolRegistry _registry;

    // Read only on a sandbox failure, to tell "this node cannot sandbox at all" from "the sandbox refused this server".
    private readonly IAgentSandboxRuntimeProvider _sandboxProvider;

    // Read only on a sandbox failure too, to tell a refusal the sandbox security profile caused from every other one.
    private readonly INodeRuntimeSettings _runtimeSettings;

    // The store is DbContext-backed and therefore Scoped, so this singleton manager resolves it per refresh through a scope rather
    // than capturing it: a captive dependency would fail ValidateOnBuild and risk concurrent DbContext use.
    private readonly IServiceScopeFactory _scopeFactory;

    // Guards _servers and every ServerEntry field. Held only for short, non-awaiting sections; connects run under the
    // entry's own gate instead, so one slow server never blocks another server's calls.
    private readonly Dictionary<Guid, ServerEntry> _servers = [];
    private readonly Lock _stateLock = new();
    private readonly TimeProvider _timeProvider;

    private long _sessionGeneration;

    private volatile bool _disposed;

    public McpServerConnectionManager(IServiceScopeFactory scopeFactory,
        IMcpToolRegistry registry,
        IMcpClientFactory clientFactory,
        IAgentSandboxRuntimeProvider sandboxProvider,
        INodeRuntimeSettings runtimeSettings,
        IOptions<McpOptions> options,
        IOptions<AgentToolPipelineOptions> pipelineOptions,
        TimeProvider timeProvider,
        ILogger<McpServerConnectionManager> logger)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _runtimeSettings = runtimeSettings;
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _sandboxProvider = sandboxProvider ?? throw new ArgumentNullException(nameof(sandboxProvider));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pipelineOptions);
        _options = options.Value;
        _maxToolResultCharacters = pipelineOptions.Value.MaxToolResultCharacters;
        _maxInvalidToolCalls = pipelineOptions.Value.MaxConsecutiveInvalidToolCallsPerTool;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idleSweepTimer = _timeProvider.CreateTimer(static state => ((McpServerConnectionManager)state!).SweepIdleConversationSessions(), this, IdleSweepInterval, IdleSweepInterval);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _idleSweepTimer.DisposeAsync();

        List<ClientSession> sessions;
        lock (_stateLock)
        {
            sessions = [.. _servers.Values.SelectMany(static entry => entry.DetachAllSessions())];
            _servers.Clear();
        }

        foreach (var session in sessions)
        {
            await DisposeSessionSafelyAsync(session);
        }

        // The refresh gate and every entry gate are deliberately NOT disposed: an in-flight refresh or call may still hold one and
        // would throw ObjectDisposedException on Release. No AvailableWaitHandle is ever created, so a SemaphoreSlim owns nothing to free.
    }

    public IReadOnlyList<McpServerConnectionStatus> GetStatuses()
    {
        lock (_stateLock)
        {
            return [.. _servers.Values.OrderBy(static entry => entry.Order).Select(static entry => entry.ToStatus())];
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var enabled = await LoadEnabledWithSlugsAsync(onlyId: null, cancellationToken);
            var enabledIds = enabled.Select(static record => record.Id).ToHashSet();

            List<Guid> stale;
            lock (_stateLock)
            {
                stale = [.. _servers.Keys.Where(id => !enabledIds.Contains(id))];
            }

            foreach (var id in stale)
            {
                await RemoveServerAsync(id);
            }

            // Servers connect in parallel: each is bounded by its own timeout and isolated from the others' failures.
            await Task.WhenAll(enabled.Select((record, order) => ReconcileAsync(record, order, cancellationToken)));
            PublishSnapshot();
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    public Task RefreshAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        return RefreshOneAsync(serverId, force: false, cancellationToken);
    }

    public Task ReconnectAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        return RefreshOneAsync(serverId, force: true, cancellationToken);
    }

    private async Task RefreshOneAsync(Guid serverId, bool force, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var enabled = await LoadEnabledWithSlugsAsync(serverId, cancellationToken);
            if (enabled.Count == 0)
            {
                // Deleted or disabled: tear down its sessions and withdraw its tools.
                await RemoveServerAsync(serverId);
            }
            else
            {
                await ReconcileAsync(enabled[0], order: null, cancellationToken, force);
            }

            PublishSnapshot();
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    /// <summary>
    ///     The routing function every MCP executable calls through: the live session for this server and the ambient
    ///     conversation (reconnected once if it ended), then the tool's result or a typed failure text.
    /// </summary>
    /// <remarks>
    ///     A conversation id is present only for chat and agent runs (<see cref="AgentRunConversationContext" />);
    ///     inbound MCP runs and unattended paths never set one and always use the shared session. A call that fails
    ///     because an HTTP server lost the session (404) is retried once on a fresh session, since the server provably
    ///     never ran it; any other transport failure is reported, never retried, because a tool call is non-idempotent.
    /// </remarks>
    internal async ValueTask<object?> InvokeRoutedAsync(Guid serverId, McpClientTool discovered, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var conversationId = AgentRunConversationContext.Current;
        var retried = false;
        while (true)
        {
            ClientSession session;
            try
            {
                session = await AcquireSessionAsync(serverId, conversationId, cancellationToken);
            }
            catch (McpServerUnavailableException exception)
            {
                // Same failure envelope the timeout wrapper uses, so observability and the tool card record an error, not a success.
                return ToolFailureText.Format(ServerUnavailableCode, exception.Message);
            }

            // Bind the discovered definition to whichever client is live now; the protocol tool keeps the server-side name.
            var tool = new McpClientTool(session.Client, discovered.ProtocolTool, discovered.JsonSerializerOptions);
            // An in-flight call pins the session against the idle sweep, and the idle window starts when the call ends.
            _ = Interlocked.Increment(ref session.ActiveCalls);
            try
            {
                var result = await tool.InvokeAsync(arguments, cancellationToken);
                Volatile.Write(ref session.ConsecutiveTimeouts, 0);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Remembered against THIS session, so a per-call timeout is charged to the session the call ran on. A caller
                // cancellation is never claimed, so the set is capped; a cleared entry only leaves one timeout uncounted.
                if (session.CancelledCalls.Count >= MaxRememberedCancelledCalls)
                {
                    session.CancelledCalls.Clear();
                }

                _ = session.CancelledCalls.TryAdd(cancellationToken, 0);
                throw;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsTransportFault(exception))
            {
                var (reason, detail, serverName) = await AbandonSessionAsync(serverId, session, exception);
                if (reason == McpConnectionFailureReason.SessionLost && !retried)
                {
                    retried = true;
                    continue;
                }

                return ToolFailureText.Format(ServerUnavailableCode, McpServerUnavailableException.NotConnected(serverName, detail).Message);
            }
            finally
            {
                _ = Interlocked.Decrement(ref session.ActiveCalls);
                session.LastUsedUtc = _timeProvider.GetUtcNow();
            }
        }
    }

    /// <summary>Failure code carried by a not-connected result; see <see cref="ToolFailureText" />.</summary>
    internal const string ServerUnavailableCode = "server_unavailable";

    /// <summary>Consecutive per-call timeouts on one session after which it is treated as wedged and abandoned.</summary>
    internal const int ConsecutiveTimeoutsBeforeAbandon = 2;

    private const int MaxRememberedCancelledCalls = 64;

    /// <summary>
    ///     Counts a per-call timeout against the current session the timed-out call ran on, found by its call token, and
    ///     abandons that session at <see cref="ConsecutiveTimeoutsBeforeAbandon" /> so the next call reconnects.
    /// </summary>
    /// <remarks>
    ///     A timeout whose session is no longer current matches nothing and is ignored, so a late timeout from a retired
    ///     session can never evict its replacement.
    /// </remarks>
    internal async ValueTask OnToolCallTimedOutAsync(Guid serverId, CancellationToken callToken)
    {
        ClientSession? session;
        lock (_stateLock)
        {
            if (!_servers.TryGetValue(serverId, out var entry))
            {
                return;
            }

            session = new[]
                {
                    entry.Primary
                }.Concat(entry.Conversations.Values)
                 .FirstOrDefault(candidate => candidate is not null && candidate.CancelledCalls.TryRemove(callToken, out _));
        }

        if (session is not { IsAlive: true } || Interlocked.Increment(ref session.ConsecutiveTimeouts) < ConsecutiveTimeoutsBeforeAbandon)
        {
            return;
        }

        _ = await AbandonSessionAsync(serverId, session,
            new TimeoutException($"{ConsecutiveTimeoutsBeforeAbandon} consecutive tool calls timed out; the session is treated as wedged."));
    }

    /// <summary>Test seam: the number of live per-conversation sessions a server holds.</summary>
    internal int CountConversationSessions(Guid serverId)
    {
        lock (_stateLock)
        {
            return _servers.TryGetValue(serverId, out var entry) ? entry.Conversations.Count : 0;
        }
    }

    /// <summary>
    ///     Loads the enabled registrations (all, or just <paramref name="onlyId" />) oldest first, assigning and
    ///     persisting a slug to any that has none.
    /// </summary>
    /// <remarks>
    ///     Assignment walks the enabled set oldest first against every slug already persisted, so the first assignment
    ///     on an upgraded node reproduces the suffixes the old per-refresh computation handed out and existing agent
    ///     allow-lists keep resolving. Once persisted a slug is never recomputed.
    /// </remarks>
    private async Task<IReadOnlyList<McpServerRecord>> LoadEnabledWithSlugsAsync(Guid? onlyId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpServerStore>();
        var all = await store.ListAsync(cancellationToken);

        var used = all.Where(static record => record.Slug is not null).Select(static record => record.Slug!).ToHashSet(StringComparer.Ordinal);
        var result = new List<McpServerRecord>();
        foreach (var record in all.Where(static record => record.Enabled))
        {
            var withSlug = record;
            if (record.Slug is null)
            {
                var slug = NextFreeSlug(Slugify(record.Name), used);
                _ = await store.AssignSlugAsync(record.Id, slug, cancellationToken);
                withSlug = record with
                {
                    Slug = slug
                };
            }

            if (onlyId is null || record.Id == onlyId)
            {
                result.Add(withSlug);
            }
        }

        return result;
    }

    private static string NextFreeSlug(string baseSlug, HashSet<string> used)
    {
        var slug = baseSlug;
        var suffix = 2;
        while (!used.Add(slug))
        {
            slug = string.Create(CultureInfo.InvariantCulture, $"{baseSlug}-{suffix}");
            suffix++;
        }

        return slug;
    }

    /// <summary>
    ///     Brings one enabled registration's shared session up to date: a live session at the same Version is kept
    ///     (only the display record is refreshed), anything else reconnects.
    /// </summary>
    /// <remarks>
    ///     A config change (Version bump) closes every session of the server and drops its old tools; <paramref name="force" />
    ///     closes every session and reconnects even a live one. A reconnect of an unchanged server that fails keeps the tools
    ///     it had, so they stay offered and fail typed.
    /// </remarks>
    private async Task ReconcileAsync(McpServerRecord record, int? order, CancellationToken cancellationToken, bool force = false)
    {
        ServerEntry entry;
        lock (_stateLock)
        {
            if (!_servers.TryGetValue(record.Id, out entry!))
            {
                entry = new ServerEntry(record, order ?? _servers.Count);
                _servers[record.Id] = entry;
            }
            else if (order is not null)
            {
                entry.Order = order.Value;
            }
        }

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            List<ClientSession> toDispose = [];
            lock (_stateLock)
            {
                var versionChanged = entry.Record.Version != record.Version;
                var alive = entry.Primary is { IsAlive: true } && entry.LastError is null;
                entry.Record = record;
                if (!versionChanged && alive && !force)
                {
                    return;
                }

                if (versionChanged || force)
                {
                    // A forced reconnect (the UI's Reconnect) also closes the per-conversation sessions: a stuck server is stuck in every
                    // session. Only a config change drops the tools; a forced reconnect that fails keeps them offered, failing typed.
                    toDispose.AddRange(entry.DetachAllSessions());
                    if (versionChanged)
                    {
                        entry.Tools = [];
                    }
                }
                else if (entry.DetachPrimary() is { } deadPrimary)
                {
                    toDispose.Add(deadPrimary);
                }
            }

            foreach (var session in toDispose)
            {
                await DisposeSessionSafelyAsync(session);
            }

            _ = await ConnectPrimaryAsync(entry, cancellationToken);
        }
        finally
        {
            _ = entry.Gate.Release();
        }
    }

    private async Task RemoveServerAsync(Guid serverId)
    {
        List<ClientSession> sessions;
        lock (_stateLock)
        {
            if (!_servers.Remove(serverId, out var entry))
            {
                return;
            }

            entry.Removed = true;
            sessions = entry.DetachAllSessions();
        }

        foreach (var session in sessions)
        {
            await DisposeSessionSafelyAsync(session);
        }
    }

    /// <summary>
    ///     Connects the server's shared session and (re)builds its offered tools from the listing. Must run under the
    ///     entry's gate. Returns <c>false</c> after recording a redacted failure on the entry.
    /// </summary>
    private async Task<bool> ConnectPrimaryAsync(ServerEntry entry, CancellationToken cancellationToken)
    {
        McpServerRecord record;
        lock (_stateLock)
        {
            record = entry.Record;
        }

        var result = await ConnectSessionAsync(record, sessionKey: null, cancellationToken);
        if (result.Session is null)
        {
            lock (_stateLock)
            {
                entry.LastError = result.Error;
                entry.Reason = result.Reason;
            }

            return false;
        }

        var session = result.Session;
        var tools = BuildRegisteredTools(record.Id, result.Tools, record.Slug!, ResolveToolCategory(record));
        lock (_stateLock)
        {
            if (!entry.Removed && !_disposed)
            {
                entry.Primary = session;
                entry.Tools = tools;
                entry.LastError = null;
                entry.Reason = null;
            }
            else
            {
                session.Closing = true;
            }
        }

        if (session.Closing)
        {
            await DisposeSessionSafelyAsync(session);
            return false;
        }

        // Only the shared session drives the catalog, so only it listens for a changed tool list.
        session.ListChangedRegistration = session.Client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (_, _) =>
        {
            OnToolListChanged(entry, session);
            return ValueTask.CompletedTask;
        });
        _ = WatchCompletionAsync(entry, session);
        return true;
    }

    /// <summary>
    ///     Connects one session under the per-server timeout and lists its tools (which also primes the HTTP client's
    ///     tool cache), returning the session or a redacted error.
    /// </summary>
    /// <remarks>
    ///     Any specific failure is isolated, so it never aborts the refresh or the other servers.
    /// </remarks>
    private async Task<ConnectResult> ConnectSessionAsync(McpServerRecord record, string? sessionKey, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

        McpClient? client = null;
        var handedOff = false;
        try
        {
            client = await _clientFactory.CreateAsync(record, sessionKey, timeoutCts.Token);
            var discovered = await client.ListToolsAsync(cancellationToken: timeoutCts.Token);
            handedOff = true;
            return new ConnectResult(new ClientSession(client, _timeProvider.GetUtcNow()), discovered, Error: null, Reason: null);
        }
        catch (SandboxCapabilityNotSupportedException ex)
        {
            // The reason tells "cannot sandbox" from "the profile refused" (the transport's own policy) from "server broken"; the exception
            // text can name a sensitive host path (a denied root, the home directory), so it stays in the log like every other failure.
            var capabilities = _sandboxProvider.Capabilities;
            var refusedByProfile = SandboxSecurityProfilePolicy.Refuses(SandboxWorkloads.McpStdio,
                capabilities,
                await _runtimeSettings.GetSandboxSecurityProfileAsync(cancellationToken),
                configRequiresEgressDenial: false);
            var reason = McpConnectionFailureReason.SandboxRefused;
            if (!capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
            {
                reason = McpConnectionFailureReason.SandboxUnavailable;
            }
            else if (refusedByProfile)
            {
                reason = McpConnectionFailureReason.SandboxRefusedByProfile;
            }

            _logger.LogWarning(ex, "MCP server {ServerId} could not be started under its trust tier ({Reason}); it will contribute no tools.", record.Id, reason);
            return ConnectResult.Failed(SafeMessage(reason), reason);
        }
        catch (Exception ex) when (ex is McpException
                                       or McpServerStartupException
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
            var reason = Classify(ex, hasHeaders: record.Headers.Count > 0);
            _logger.LogWarning(ex, "MCP server {ServerId} failed to connect or list tools ({Reason}); it will contribute no tools.", record.Id, reason);
            return ConnectResult.Failed(DescribeFailure(ex, reason, record), reason);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The per-server timeout fired (not a caller cancel). Treat it like any other isolated failure.
            _logger.LogWarning("MCP server {ServerId} timed out after {TimeoutSeconds}s; it will contribute no tools.", record.Id, _options.ConnectTimeoutSeconds);
            return ConnectResult.Failed(SafeMessage(McpConnectionFailureReason.Timeout), McpConnectionFailureReason.Timeout);
        }
        finally
        {
            // Every path that does not hand the session back disposes it — a caller cancel included, which no catch above sees and
            // which would otherwise orphan the connected child process or sandbox jail.
            if (!handedOff)
            {
                await DisposePartialClientAsync(client);
            }
        }
    }

    /// <summary>
    ///     Returns the live session a call should use, reconnecting once when it has ended, or throws
    ///     <see cref="McpServerUnavailableException" /> with a model-facing reason.
    /// </summary>
    private async Task<ClientSession> AcquireSessionAsync(Guid serverId, Guid? conversationId, CancellationToken cancellationToken)
    {
        ServerEntry? entry;
        lock (_stateLock)
        {
            _ = _servers.TryGetValue(serverId, out entry);
        }

        if (entry is null)
        {
            throw new McpServerUnavailableException("This MCP server is no longer enabled on this node, so its tools cannot be called.");
        }

        // A live session needs no gate: only a connect is serialized, so a call never queues behind another conversation's connect.
        lock (_stateLock)
        {
            var live = conversationId is not null && entry.Record.SessionScope == McpSessionScope.PerConversation
                ? entry.Conversations.GetValueOrDefault(conversationId.Value)
                : entry.Primary;
            if (!entry.Removed && live is { IsAlive: true })
            {
                live.LastUsedUtc = _timeProvider.GetUtcNow();
                return live;
            }
        }

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            McpServerRecord record;
            ClientSession? existing;
            var perConversation = false;
            lock (_stateLock)
            {
                record = entry.Record;
                if (entry.Removed)
                {
                    throw McpServerUnavailableException.NotConnected(record.Name, "It was disabled or removed.");
                }

                perConversation = conversationId is not null && record.SessionScope == McpSessionScope.PerConversation;
                existing = perConversation
                    ? entry.Conversations.GetValueOrDefault(conversationId!.Value)
                    : entry.Primary;
                if (existing is { IsAlive: true })
                {
                    existing.LastUsedUtc = _timeProvider.GetUtcNow();
                    return existing;
                }

                if (perConversation)
                {
                    _ = entry.Conversations.Remove(conversationId!.Value);
                    if (record.TransportKind == McpTransportKind.Stdio && entry.Conversations.Count >= MaxConversationSessionsPerStdioServer)
                    {
                        throw new McpServerUnavailableException(string.Create(CultureInfo.InvariantCulture,
                            $"MCP server '{record.Name}' already runs {MaxConversationSessionsPerStdioServer} per-conversation sessions, the limit for a stdio server. Try again after another conversation has been idle for {ConversationSessionIdleTimeout.TotalMinutes:0} minutes."));
                    }
                }
                else
                {
                    _ = entry.DetachPrimary();
                }
            }

            if (existing is not null)
            {
                await DisposeSessionSafelyAsync(existing);
            }

            if (!perConversation)
            {
                if (await ConnectPrimaryAsync(entry, cancellationToken))
                {
                    PublishSnapshot();
                    lock (_stateLock)
                    {
                        return entry.Primary!;
                    }
                }

                lock (_stateLock)
                {
                    throw McpServerUnavailableException.NotConnected(record.Name, entry.LastError ?? SafeMessage(McpConnectionFailureReason.Unknown));
                }
            }

            // The conversation id plus a generation keys the session, so a Sandboxed stdio server runs each conversation in
            // its own jail AND a replacement never shares the identity of a retired session still being torn down.
            var generation = Interlocked.Increment(ref _sessionGeneration);
            var result = await ConnectSessionAsync(record, string.Create(CultureInfo.InvariantCulture, $"{conversationId!.Value:N}-{generation}"), cancellationToken);
            if (result.Session is null)
            {
                throw McpServerUnavailableException.NotConnected(record.Name, result.Error ?? SafeMessage(McpConnectionFailureReason.Unknown));
            }

            var session = result.Session;
            lock (_stateLock)
            {
                if (!entry.Removed && !_disposed)
                {
                    entry.Conversations[conversationId.Value] = session;
                }
                else
                {
                    session.Closing = true;
                }
            }

            if (session.Closing)
            {
                await DisposeSessionSafelyAsync(session);
                throw McpServerUnavailableException.NotConnected(record.Name, "It was disabled or removed.");
            }

            _ = WatchCompletionAsync(entry, session);
            return session;
        }
        finally
        {
            _ = entry.Gate.Release();
        }
    }

    /// <summary>
    ///     Retires a session a call just failed on and records why, so the next call reconnects instead of reusing it.
    /// </summary>
    private async Task<AbandonedSession> AbandonSessionAsync(Guid serverId, ClientSession session, Exception exception)
    {
        McpServerRecord? record;
        lock (_stateLock)
        {
            record = _servers.TryGetValue(serverId, out var found) ? found.Record : null;
        }

        var (reason, detail) = session.Client.Completion.IsCompleted
            ? DescribeCompletion(await session.Client.Completion, record)
            : new SessionFailure(Classify(exception, hasHeaders: record?.Headers.Count > 0, inSession: true), string.Empty);
        if (detail.Length == 0)
        {
            detail = SafeMessage(reason);
        }

        _logger.LogWarning(exception, "MCP server {ServerId} failed a tool call on a dead session ({Reason}); the next call reconnects.", serverId, reason);
        lock (_stateLock)
        {
            if (_servers.TryGetValue(serverId, out var entry))
            {
                if (ReferenceEquals(entry.Primary, session))
                {
                    _ = entry.DetachPrimary();
                    entry.LastError = detail;
                    entry.Reason = reason;
                }
                else
                {
                    entry.RemoveConversationSession(session);
                }
            }

            session.Closing = true;
        }

        await DisposeSessionSafelyAsync(session);
        return new AbandonedSession(reason, detail, record?.Name ?? "unknown");
    }

    private readonly record struct AbandonedSession(McpConnectionFailureReason Reason, string Detail, string ServerName);

    private readonly record struct SessionFailure(McpConnectionFailureReason Reason, string Detail);

    private static bool IsTransportFault(Exception exception)
    {
        // A protocol error (unknown tool, invalid params) is the server answering; everything else here means the session is gone.
        return exception is IOException or HttpRequestException or SocketException or ObjectDisposedException
               || (exception is McpException && exception is not McpProtocolException);
    }

    /// <summary>
    ///     Waits for a session's client to complete and, unless this node closed it, records why: a dead shared session
    ///     puts the server into the error state (its tools stay offered), a dead conversation session is forgotten.
    /// </summary>
    private async Task WatchCompletionAsync(ServerEntry entry, ClientSession session)
    {
        var details = await session.Client.Completion;
        McpServerRecord record;
        lock (_stateLock)
        {
            if (session.Closing || _disposed)
            {
                return;
            }

            record = entry.Record;
        }

        var (reason, detail) = DescribeCompletion(details, record);
        _logger.LogWarning(details.Exception, "MCP server {ServerId} session ended ({Reason}); its tools stay offered and the next call reconnects.", record.Id, reason);
        lock (_stateLock)
        {
            if (ReferenceEquals(entry.Primary, session))
            {
                entry.LastError = detail;
                entry.Reason = reason;
                return;
            }

            entry.RemoveConversationSession(session);
        }

        // Nothing else holds a forgotten conversation session, so it is disposed here: that releases its sandbox jail and execution id,
        // which the conversation's next session reuses.
        await DisposeSessionSafelyAsync(session);
    }

    private static SessionFailure DescribeCompletion(ClientCompletionDetails details, McpServerRecord? record)
    {
        return details switch
        {
            StdioClientCompletionDetails stdio => new SessionFailure(McpConnectionFailureReason.ServerExited,
                AppendStderrTail(string.Create(CultureInfo.InvariantCulture,
                        $"{SafeMessage(McpConnectionFailureReason.ServerExited)} Exit code {stdio.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}."),
                    JoinTail(stdio.StandardErrorTail), record)),
            HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.NotFound } => new SessionFailure(McpConnectionFailureReason.SessionLost, SafeMessage(McpConnectionFailureReason.SessionLost)),
            HttpClientCompletionDetails => new SessionFailure(McpConnectionFailureReason.Transport, SafeMessage(McpConnectionFailureReason.Transport)),
            _ => new SessionFailure(McpConnectionFailureReason.ServerExited, SafeMessage(McpConnectionFailureReason.ServerExited))
        };
    }

    private static string? JoinTail(IEnumerable<string>? lines)
    {
        return lines is null ? null : string.Join('\n', lines);
    }

    /// <summary>
    ///     Adds a <c>stderr:</c> line with the capped tail (unless the message already quotes it), then scrubs the
    ///     registration's own secret values from the whole text before it reaches the UI or the model.
    /// </summary>
    private static string AppendStderrTail(string message, string? tail, McpServerRecord? record)
    {
        var text = Scrub(message, record);
        if (!string.IsNullOrWhiteSpace(tail) && !message.Contains(tail.Trim(), StringComparison.Ordinal))
        {
            // Scrub the WHOLE tail before the display cut, or the cut could split a secret and leave a matching-free suffix.
            var trimmed = Scrub(tail.Trim(), record);
            if (trimmed.Length > MaxStderrTailCharacters)
            {
                trimmed = string.Concat("...", trimmed.AsSpan(trimmed.Length - MaxStderrTailCharacters));
            }

            text = $"{text}\nstderr: {trimmed}";
        }

        return text;
    }

    private static string Scrub(string text, McpServerRecord? record)
    {
        // Value-based with an 8-char floor, so "-y" never blanks output (a shorter secret can survive); each long part of a
        // multi-word value is redacted too. PATH is not a secret, and the FileNotFound hint quotes it.
        var values = record is null
            ? Enumerable.Empty<string>()
            : record.Environment.Where(static pair => !SecretValueRedactor.IsSearchPathKey(pair.Key))
                    .Select(static pair => pair.Value)
                    .Concat(record.Headers.Values)
                    .Concat(record.Arguments);
        return new SecretValueRedactor(SecretValueRedactor.WithParts(values, minLength: 8)).Redact(text);
    }

    /// <summary>
    ///     The status text for a failed connect: fixed wording per reason, except the two whose exception message is
    ///     written for the operator (the Sandboxed PATH hint, the startup failure) and so is shown, scrubbed.
    /// </summary>
    private static string DescribeFailure(Exception exception, McpConnectionFailureReason reason, McpServerRecord record)
    {
        return exception switch
        {
            FileNotFoundException notFound => Scrub(notFound.Message, record),
            McpServerStartupException startup => AppendStderrTail(startup.Message, startup.StderrTail, record),
            ClientTransportClosedException { Details: StdioClientCompletionDetails stdio } => AppendStderrTail(SafeMessage(reason), JoinTail(stdio.StandardErrorTail), record),
            _ => SafeMessage(reason)
        };
    }

    private void OnToolListChanged(ServerEntry entry, ClientSession session)
    {
        int generation;
        lock (_stateLock)
        {
            generation = ++entry.RelistGeneration;
        }

        _ = RelistAfterDebounceAsync(entry, session, generation);
    }

    /// <summary>
    ///     Re-lists a server's tools once a burst of <c>tools/list_changed</c> notifications has settled, and republishes
    ///     the snapshot, so a server that adds or removes tools needs no toggle.
    /// </summary>
    private async Task RelistAfterDebounceAsync(ServerEntry entry, ClientSession session, int generation)
    {
        try
        {
            await Task.Delay(ToolListChangedDebounce, _timeProvider, CancellationToken.None);
            lock (_stateLock)
            {
                if (_disposed || generation != entry.RelistGeneration)
                {
                    return;
                }
            }

            await entry.Gate.WaitAsync(CancellationToken.None);
            try
            {
                McpServerRecord record;
                lock (_stateLock)
                {
                    if (!ReferenceEquals(entry.Primary, session) || !session.IsAlive)
                    {
                        return;
                    }

                    record = entry.Record;
                }

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
                var discovered = await session.Client.ListToolsAsync(cancellationToken: timeoutCts.Token);
                var tools = BuildRegisteredTools(record.Id, discovered, record.Slug!, ResolveToolCategory(record));
                lock (_stateLock)
                {
                    if (ReferenceEquals(entry.Primary, session))
                    {
                        entry.Tools = tools;
                    }
                }

                PublishSnapshot();
            }
            finally
            {
                _ = entry.Gate.Release();
            }
        }
        catch (Exception ex) when (ex is McpException or IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
            _logger.LogWarning(ex, "Re-listing the tools of an MCP server after tools/list_changed failed; its previous tools stay offered.");
        }
    }

    private void SweepIdleConversationSessions()
    {
        var cutoff = _timeProvider.GetUtcNow() - ConversationSessionIdleTimeout;
        List<ClientSession> expired = [];
        lock (_stateLock)
        {
            var idle = _servers.Values
                               .SelectMany(static entry => entry.Conversations.Select(pair => new
                               {
                                   Entry = entry,
                                   ConversationId = pair.Key,
                                   Session = pair.Value
                               }))
                               .Where(item => item.Session.LastUsedUtc <= cutoff && Volatile.Read(ref item.Session.ActiveCalls) == 0)
                               .ToList();
            foreach (var item in idle)
            {
                _ = item.Entry.Conversations.Remove(item.ConversationId);
                item.Session.Closing = true;
                expired.Add(item.Session);
            }
        }

        foreach (var session in expired)
        {
            _ = DisposeSessionSafelyAsync(session).AsTask();
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
    ///     offer descriptor with approval on by default, and wraps an executable that routes through
    ///     <see cref="InvokeRoutedAsync" />.
    /// </summary>
    /// <remarks>
    ///     The wrapping bounds the server round-trip with the per-call timeout, the result with the shared tool-result
    ///     budget, and the executable in an approval gate. Approval itself is unaffected by the risk class: every MCP
    ///     tool is approval-required and ineligible for a remembered session approval.
    /// </remarks>
    private IReadOnlyList<McpRegisteredTool> BuildRegisteredTools(Guid serverId, IList<McpClientTool> discovered, string slug, ToolCategory category)
    {
        var toolCallTimeout = TimeSpan.FromSeconds(_options.ToolCallTimeoutSeconds);
        var registered = new List<McpRegisteredTool>(discovered.Count);
        foreach (var tool in discovered)
        {
            var qualifiedName = $"mcp__{slug}__{tool.Name}";
            AIFunction named = new RoutedMcpToolFunction(this, serverId, tool, tool.WithName(qualifiedName));

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
            AIFunction timed = new McpToolCallTimeoutAIFunction(named, toolCallTimeout, callToken => OnToolCallTimedOutAsync(serverId, callToken));

            // Validate the model's arguments against the tool's schema and run the repair loop before the server sees them, the guard
            // the ClientLocal registry applies. Unknown-property rejection is OFF: an under-declared server schema must not bounce a needed key.
            AIFunction validated = new ToolArgumentRepairAIFunction(timed, _maxInvalidToolCalls, rejectUnknownProperties: false);

            // Backstop the model-shaped MCP result with the shared budget UNDER the approval gate, so a server can't
            // flood chat history and ApprovalRequiredAIFunction stays the outermost type the approval pipeline detects.
            AIFunction budgeted = new BudgetedToolResultAIFunction(WrapForModel(validated), _maxToolResultCharacters);
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
    ///     The single insertion point for the projection that turns a raw MCP result into the shape the model reads,
    ///     applied inside the result budget so the budget measures what the model actually receives.
    /// </summary>
    private static AIFunction WrapForModel(AIFunction function)
    {
        return new McpToolResultProjectionAIFunction(function);
    }

    /// <summary>
    ///     Builds the full, deterministically ordered tool list from every server and republishes it to the registry in
    ///     one atomic snapshot swap.
    /// </summary>
    /// <remarks>
    ///     Built and swapped under the state lock, so two publishers can never interleave an older snapshot over a newer one.
    /// </remarks>
    private void PublishSnapshot()
    {
        lock (_stateLock)
        {
            var all = _servers.Values
                              .SelectMany(static server => server.Tools)
                              .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                              .ToList();

            _registry.ReplaceSnapshot(all);
        }
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
    ///     Maps a caught connect, list or call failure to the reason the management panel words.
    /// </summary>
    /// <remarks>
    ///     A missing command is a <see cref="Win32Exception" /> wrapped in an <see cref="IOException" /> (SDK launch) or a
    ///     <see cref="FileNotFoundException" /> (Sandboxed pre-check); a process that died before the handshake is
    ///     <see cref="McpConnectionFailureReason.ServerStartupFailed" />. Order matters: all are <see cref="IOException" />s.
    ///     A 401 means "no credential" when the registration sends none and "wrong credential" when it does.
    /// </remarks>
    internal static McpConnectionFailureReason Classify(Exception exception, bool hasHeaders, bool inSession = false)
    {
        return exception switch
        {
            FileNotFoundException or IOException { InnerException: Win32Exception } => McpConnectionFailureReason.ServerNotFound,
            McpServerStartupException or ClientTransportClosedException { Details: StdioClientCompletionDetails } => McpConnectionFailureReason.ServerStartupFailed,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => hasHeaders ? McpConnectionFailureReason.Authentication : McpConnectionFailureReason.AuthenticationRequired,
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => McpConnectionFailureReason.Forbidden,
            // Mid-session the server no longer knows the session: SessionLost, so the call's one retry does not hinge on Completion
            // having settled. At connect time a 404 is a wrong endpoint and stays Transport.
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } when inSession => McpConnectionFailureReason.SessionLost,
            AuthenticationException or HttpRequestException { InnerException: AuthenticationException } => McpConnectionFailureReason.Tls,
            TimeoutException => McpConnectionFailureReason.Timeout,
            HttpRequestException or IOException or SocketException => McpConnectionFailureReason.Transport,
            McpException or JsonException => McpConnectionFailureReason.Protocol,
            _ => McpConnectionFailureReason.Unknown
        };
    }

    internal static string SafeMessage(McpConnectionFailureReason reason)
    {
        // Connection, transport and sandbox messages can echo a command path, URL or protected host path, so the exception text
        // never reaches the UI: each reason has one fixed wording, and the real exception is logged server-side.
        return reason switch
        {
            McpConnectionFailureReason.SandboxUnavailable => OperatingSystem.IsWindows()
                ? "This node cannot isolate the MCP server from the host filesystem. On Windows that needs the MXC AppContainer boundary (Preview) available and execution previews enabled in Node settings, or move the server to the Privileged host tier."
                : "This node cannot isolate the MCP server from the host filesystem. Install bubblewrap (bwrap) with user-namespace support, or move the server to the Privileged host tier.",
            McpConnectionFailureReason.SandboxRefused =>
                "The sandbox refused to start the MCP server: its command or working directory overlaps a protected location, or the sandbox boundary could not be established. Point it at the directory holding the server's own files.",
            McpConnectionFailureReason.SandboxRefusedByProfile =>
                "The node's sandbox security profile is 'high', and this node's sandbox cannot impose the CPU, memory and process-count ceilings that profile requires for a Sandboxed MCP server. Install the missing mechanism the sandbox isolation summary names, or "
                + SandboxSecurityProfilePolicy.Remedy + ".",
            McpConnectionFailureReason.ServerNotFound => "The MCP server's command was not found or could not be started.",
            McpConnectionFailureReason.Authentication => "The MCP server rejected the configured credentials.",
            McpConnectionFailureReason.AuthenticationRequired => "The MCP server requires authentication, and no credential is configured. Add an Authorization header to the registration.",
            McpConnectionFailureReason.Forbidden => "The MCP server refused access with the configured credentials.",
            McpConnectionFailureReason.Tls => "The TLS negotiation with the MCP server failed.",
            McpConnectionFailureReason.Timeout => "Timed out connecting to the MCP server.",
            McpConnectionFailureReason.Transport => "The connection to the MCP server failed or closed.",
            McpConnectionFailureReason.Protocol => "The MCP server did not complete the MCP handshake.",
            McpConnectionFailureReason.ServerExited => "The MCP server process exited.",
            McpConnectionFailureReason.SessionLost => "The MCP server no longer recognises this node's session; it has probably restarted.",
            McpConnectionFailureReason.ServerStartupFailed => "The MCP server exited before completing the MCP handshake.",
            _ => "The MCP server connection failed."
        };
    }

    private async ValueTask DisposePartialClientAsync(McpClient? client)
    {
        if (client is not null)
        {
            await DisposeSessionSafelyAsync(new ClientSession(client, _timeProvider.GetUtcNow())
            {
                Closing = true
            });
        }
    }

    private async ValueTask DisposeSessionSafelyAsync(ClientSession session)
    {
        session.Closing = true;
        try
        {
            if (session.ListChangedRegistration is { } registration)
            {
                await registration.DisposeAsync();
            }

            await session.Client.DisposeAsync();
        }
        catch (Exception ex) when (ex is McpException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Ignored error while disposing an MCP client session.");
        }
    }

    /// <summary>One MCP client session and the bookkeeping the router, the completion watch and the idle sweep share.</summary>
    private sealed class ClientSession
    {
        public ClientSession(McpClient client, DateTimeOffset lastUsedUtc)
        {
            Client = client;
            LastUsedUtc = lastUsedUtc;
        }

        public McpClient Client { get; }

        public DateTimeOffset LastUsedUtc { get; set; }

        /// <summary>Calls running on this session right now; the idle sweep never retires a session with one in flight.</summary>
        public int ActiveCalls;

        /// <summary>Per-call timeouts since the last call that answered; reaching the limit abandons the session.</summary>
        public int ConsecutiveTimeouts;

        /// <summary>Tokens of calls this session saw cancelled, so a timeout is charged to the session that ran it.</summary>
        public ConcurrentDictionary<CancellationToken, byte> CancelledCalls { get; } = new();

        /// <summary>Set when this node retires the session, so its completion is not reported as a server failure.</summary>
        public bool Closing { get; set; }

        public IAsyncDisposable? ListChangedRegistration { get; set; }

        public bool IsAlive => !Closing && !Client.Completion.IsCompleted;
    }

    /// <summary>
    ///     Everything the manager knows about one enabled registration. Fields are read and written under the manager's
    ///     state lock; <see cref="Gate" /> serializes the slow connect paths for this server only.
    /// </summary>
    private sealed class ServerEntry
    {
        public ServerEntry(McpServerRecord record, int order)
        {
            Record = record;
            Order = order;
        }

        public SemaphoreSlim Gate { get; } = new(initialCount: 1, maxCount: 1);

        public McpServerRecord Record { get; set; }

        public int Order { get; set; }

        public ClientSession? Primary { get; set; }

        public Dictionary<Guid, ClientSession> Conversations { get; } = [];

        public IReadOnlyList<McpRegisteredTool> Tools { get; set; } = [];

        public string? LastError { get; set; }

        public McpConnectionFailureReason? Reason { get; set; }

        public bool Removed { get; set; }

        public int RelistGeneration { get; set; }

        public ClientSession? DetachPrimary()
        {
            var primary = Primary;
            Primary = null;
            if (primary is not null)
            {
                primary.Closing = true;
            }

            return primary;
        }

        public List<ClientSession> DetachAllSessions()
        {
            List<ClientSession> sessions = [.. Conversations.Values];
            Conversations.Clear();
            if (DetachPrimary() is { } primary)
            {
                sessions.Add(primary);
            }

            foreach (var session in sessions)
            {
                session.Closing = true;
            }

            return sessions;
        }

        public void RemoveConversationSession(ClientSession session)
        {
            foreach (var (conversationId, candidate) in Conversations)
            {
                if (ReferenceEquals(candidate, session))
                {
                    _ = Conversations.Remove(conversationId);
                    return;
                }
            }
        }

        public McpServerConnectionStatus ToStatus()
        {
            var connected = Primary is not null && LastError is null;
            IReadOnlyList<McpServerToolInfo> tools = connected
                ?
                [
                    .. Tools.Select(static tool => new McpServerToolInfo
                    {
                        Name = tool.Name,
                        Description = tool.Descriptor.Description,
                        RequiresApproval = tool.Descriptor.RequiresApproval
                    })
                ]
                : [];

            return new McpServerConnectionStatus
            {
                ServerId = Record.Id,
                Name = Record.Name,
                Connected = connected,
                ToolCount = tools.Count,
                LastError = connected ? null : LastError,
                FailureReason = connected ? null : Reason,
                Tools = tools
            };
        }
    }

    /// <summary>
    ///     A published MCP executable: name, description and schema come from the discovered tool renamed to its
    ///     qualified name, and every invocation routes through the manager to whichever session is live.
    /// </summary>
    private sealed class RoutedMcpToolFunction : DelegatingAIFunction
    {
        private readonly McpClientTool _discovered;
        private readonly McpServerConnectionManager _manager;
        private readonly Guid _serverId;

        public RoutedMcpToolFunction(McpServerConnectionManager manager, Guid serverId, McpClientTool discovered, AIFunction named)
            : base(named)
        {
            _manager = manager;
            _serverId = serverId;
            _discovered = discovered;
        }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            return _manager.InvokeRoutedAsync(_serverId, _discovered, arguments, cancellationToken);
        }
    }

    private sealed record ConnectResult(ClientSession? Session, IList<McpClientTool> Tools, string? Error, McpConnectionFailureReason? Reason)
    {
        public static ConnectResult Failed(string error, McpConnectionFailureReason reason)
        {
            return new ConnectResult(Session: null, [], error, reason);
        }
    }
}
