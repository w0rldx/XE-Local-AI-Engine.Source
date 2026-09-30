namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using System.Collections.Frozen;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class McpServerService : IMcpServerService
{
    private const int MaxHeaderValueBytes = 4096;

    private static readonly FrozenSet<string> ReservedHeaderNames = FrozenSet.ToFrozenSet(["Host", "Content-Length"], StringComparer.OrdinalIgnoreCase);

    private readonly IMcpServerConnectionManager _connectionManager;
    private readonly ILogger<McpServerService> _logger;
    private readonly IOptions<McpOptions> _mcpOptions;
    private readonly IMcpServerStore _store;

    public McpServerService(IMcpServerStore store,
        IMcpServerConnectionManager connectionManager,
        IOptions<McpOptions> mcpOptions,
        ILogger<McpServerService> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mcpOptions);
        ArgumentNullException.ThrowIfNull(store);
        _connectionManager = connectionManager;
        _logger = logger;
        _mcpOptions = mcpOptions;
        _store = store;
    }

    public async Task<McpServerRecord> CreateAsync(McpServerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        Validate(input);
        input = NormalizeTrustTier(input);
        await EnsureNameAvailableAsync(input.Name, excludeId: null, cancellationToken);

        try
        {
            // The store forces Enabled = false on create, so the enabled set is unchanged and no refresh is needed.
            return await _store.AddAsync(input, cancellationToken);
        }
        catch (McpServerNameConflictException exception)
        {
            // The unique Name index is the backstop when a concurrent create races past the pre-check above.
            throw new McpServerValidationException($"An MCP server named '{input.Name}' is already registered.", exception);
        }
    }

    public async Task<McpServerRecord?> UpdateAsync(Guid id, McpServerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        Validate(input);
        input = NormalizeTrustTier(input);

        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        await EnsureNameAvailableAsync(input.Name, id, cancellationToken);

        // A PUT edit never flips the enabled state, which is SetEnabledAsync's job, so the stored flag carries through whatever the body
        // claims. Masked environment values are restored from the record, so a form round-tripping what it was shown cannot erase a secret.
        var edit = input with
        {
            Environment = RestoreMasked(input.Environment, existing.Environment),
            Headers = RestoreMasked(input.Headers, existing.Headers),
            Url = McpUrlMask.Restore(input.Url, existing.Url),
            Enabled = existing.Enabled
        };

        McpServerRecord? updated;
        try
        {
            updated = await _store.UpdateAsync(id, edit, cancellationToken);
        }
        catch (McpServerNameConflictException exception)
        {
            // The unique Name index is the backstop when a concurrent rename races past the pre-check above.
            throw new McpServerValidationException($"An MCP server named '{input.Name}' is already registered.", exception);
        }

        if (updated is null)
        {
            return null;
        }

        // Only an enabled server has a live connection to refresh, and the store bumps Version only for a change that affects
        // how it connects. A rename keeps the live session but still refreshes, so status and failure texts carry the new name.
        if (updated.Enabled && (updated.Version != existing.Version || !string.Equals(updated.Name, existing.Name, StringComparison.Ordinal)))
        {
            await RefreshConnectionsAsync(id, cancellationToken);
        }

        return updated;
    }

    public async Task<McpServerRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.Enabled == enabled)
        {
            // Enabling an already-enabled server is the UI's Reconnect: close and re-open every session of that one server, even a
            // live-looking one, without a store write or Version bump. Disabling a disabled server has nothing to tear down.
            if (enabled)
            {
                await RefreshConnectionsAsync(id, cancellationToken, reconnect: true);
            }

            return existing;
        }

        // Flip only the enabled flag via the dedicated store method: it touches just the flag (single Version bump,
        // timestamp) and leaves the encrypted secret columns untouched, so a toggle never re-encrypts args/env/description.
        var updated = await _store.SetEnabledAsync(id, enabled, cancellationToken);
        if (updated is null)
        {
            return null;
        }

        // The enabled set changed (a server was connected or disconnected), so re-publish the live tool snapshot.
        await RefreshConnectionsAsync(id, cancellationToken);

        return updated;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        var deleted = await _store.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return false;
        }

        // Removing an enabled server shrinks the connected set; a disabled server had no live connection to tear down.
        if (existing.Enabled)
        {
            await RefreshConnectionsAsync(id, cancellationToken);
        }

        return true;
    }

    public Task<McpServerRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.GetByIdAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListAsync(cancellationToken);
    }

    public async Task<McpServerToolsView?> GetToolsViewAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetByIdAsync(id, cancellationToken);
        if (record is null)
        {
            return null;
        }

        // A disabled server is never connected, so report disabled regardless of any stale status entry.
        if (!record.Enabled)
        {
            return new McpServerToolsView
            {
                Status = McpServerToolsStatus.Disabled,
                Error = null,
                Tools = []
            };
        }

        var status = _connectionManager.GetStatuses().FirstOrDefault(entry => entry.ServerId == record.Id);
        if (status is { Connected: true })
        {
            return new McpServerToolsView
            {
                Status = McpServerToolsStatus.Connected,
                Error = null,
                Tools = status.Tools
            };
        }

        // Enabled but not connected. Only an actually recorded failure — a status entry exists, the server is not connected, and the connection manager captured a redacted
        // reason — is a hard "error"; otherwise the server is still "connecting", which keeps a healthy not-yet-connected server from showing as a failure in the UI.
        return status is { LastError: { Length: > 0 } recordedError }
            ? new McpServerToolsView
            {
                Status = McpServerToolsStatus.Error,
                Error = recordedError,
                FailureReason = status.FailureReason,
                Tools = []
            }
            : new McpServerToolsView
            {
                Status = McpServerToolsStatus.Connecting,
                Error = null,
                Tools = []
            };
    }

    private void Validate(McpServerInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new McpServerValidationException("Name is required.");
        }

        if (!Enum.IsDefined(input.TransportKind))
        {
            throw new McpServerValidationException($"Transport '{input.TransportKind}' is not a valid MCP transport.");
        }

        if (!Enum.IsDefined(input.SessionScope))
        {
            throw new McpServerValidationException($"Session scope '{input.SessionScope}' is not a valid MCP session scope.");
        }

        if (!Enum.IsDefined(input.TrustTier))
        {
            throw new McpServerValidationException($"Trust tier '{input.TrustTier}' is not a valid MCP trust tier.");
        }

        if (input.TrustTier == McpTrustTier.BuiltInTrusted)
        {
            // BuiltInTrusted names a transport the ENGINE owns, and nothing registered through this surface is one: accepting it would
            // let anything holding a session label a third-party executable engine-owned. Refused, not downgraded, so the attempt shows.
            throw new McpServerValidationException("Trust tier 'BuiltInTrusted' is reserved for engine-owned MCP transports and cannot be assigned to a registration.");
        }

        switch (input.TransportKind)
        {
            case McpTransportKind.Stdio:
                if (string.IsNullOrWhiteSpace(input.Command))
                {
                    throw new McpServerValidationException("Command is required for a stdio MCP server.");
                }

                if (input.Headers.Count > 0)
                {
                    throw new McpServerValidationException("Headers apply only to an HTTP MCP server; pass a stdio server's credentials as environment variables.");
                }

                break;

            case McpTransportKind.Http:
                ValidateHttpUrl(input.Url);
                if (input.Environment.Count > 0)
                {
                    // The HTTP transport launches nothing, so an environment would be stored and silently never sent.
                    throw new McpServerValidationException("Environment variables apply only to a stdio MCP server; send an HTTP server's credentials as headers.");
                }

                ValidateHeaders(input.Headers);
                break;

            default:
                throw new McpServerValidationException($"Transport '{input.TransportKind}' is not supported.");
        }
    }

    /// <summary>
    ///     Replaces every environment or header value the caller sent as <see cref="McpEnvironmentMask.Value" /> with
    ///     the value already stored under that key.
    /// </summary>
    /// <remarks>
    ///     A key that carries the mask with no stored value is refused: there is nothing to restore, and storing the mask
    ///     as the value would hand the server a placeholder instead of the credential the operator meant to type.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> RestoreMasked(IReadOnlyDictionary<string, string> incoming,
        IReadOnlyDictionary<string, string> stored)
    {
        var restored = new Dictionary<string, string>(incoming.Count, StringComparer.Ordinal);
        foreach (var (key, value) in incoming)
        {
            if (!string.Equals(value, McpEnvironmentMask.Value, StringComparison.Ordinal))
            {
                restored[key] = value;
                continue;
            }

            restored[key] = stored.TryGetValue(key, out var storedValue)
                ? storedValue
                : throw new McpServerValidationException($"'{key}' is new and carries the masked placeholder; enter its value.");
        }

        return restored;
    }

    /// <summary>
    ///     The tier answers "where does this server's PROCESS run", so it is inert for HTTP: this node launches
    ///     nothing for an HTTP registration, it opens a loopback socket to a server already running.
    /// </summary>
    /// <remarks>
    ///     An HTTP row is therefore stored at the column default rather than at whatever the request carried, so a
    ///     persisted <see cref="McpTrustTier.PrivilegedHost" /> can never read as a host grant somebody actually made.
    /// </remarks>
    private static McpServerInput NormalizeTrustTier(McpServerInput input)
    {
        return input.TransportKind == McpTransportKind.Http
            ? input with
            {
                TrustTier = McpTrustTier.Sandboxed
            }
            : input;
    }

    private void ValidateHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new McpServerValidationException("Url is required for an HTTP MCP server.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new McpServerValidationException("Url must be an absolute http or https URL.");
        }

        if (uri.UserInfo.Length > 0)
        {
            // Userinfo is never sent as a credential by the transport and would be stored in plaintext: headers carry credentials.
            throw new McpServerValidationException("Url must not carry credentials (user:password@); configure an Authorization header instead.");
        }

        // The HTTP transport is loopback-only by default, the connection manager re-checking at connect time. The allow-list matches the
        // URL host case-insensitively, brackets stripped from an IPv6 literal as the factory does, so both sides accept the bare address.
        var host = uri.Host.Trim('[', ']');
        var loopbackHosts = _mcpOptions.Value.HttpLoopbackHosts ?? [];
        var hostAllowed = loopbackHosts.Any(allowed => string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase));
        if (!hostAllowed)
        {
            throw new McpServerValidationException($"Url host '{host}' is not in the allowed loopback set ({string.Join(", ", loopbackHosts)}).");
        }
    }

    /// <summary>
    ///     Header names must be HTTP tokens and not ones the transport owns; values are single-line and bounded.
    /// </summary>
    /// <remarks>
    ///     A masked value (<see cref="McpEnvironmentMask.Value" />) passes: it is restored from the stored header
    ///     before anything is saved.
    /// </remarks>
    private static void ValidateHeaders(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (name.Length == 0 || !name.All(IsHeaderTokenChar))
            {
                throw new McpServerValidationException($"Header name '{name}' is not a valid HTTP header name.");
            }

            if (ReservedHeaderNames.Contains(name))
            {
                throw new McpServerValidationException($"Header '{name}' is set by the transport and cannot be configured.");
            }

            if (Encoding.UTF8.GetByteCount(value) > MaxHeaderValueBytes)
            {
                throw new McpServerValidationException($"The value of header '{name}' exceeds {MaxHeaderValueBytes} bytes.");
            }

            if (value.Any(static ch => char.IsControl(ch) && ch != '\t'))
            {
                throw new McpServerValidationException($"The value of header '{name}' must be a single line without control characters.");
            }
        }
    }

    private static bool IsHeaderTokenChar(char ch)
    {
        // RFC 9110 token: visible ASCII letters and digits plus the listed punctuation.
        return char.IsAsciiLetterOrDigit(ch) || "!#$%&'*+-.^_`|~".Contains(ch, StringComparison.Ordinal);
    }

    private async Task EnsureNameAvailableAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        // Pre-check against the current registrations so the common case returns a friendly validation error, the unique index being the
        // backstop for a race. Name uniqueness is case-insensitive, because the qualified tool-name slug derives from it.
        var existing = await _store.ListAsync(cancellationToken);
        var clash = existing.Any(record => record.Id != excludeId
                                           && string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash)
        {
            throw new McpServerValidationException($"An MCP server named '{name}' is already registered.");
        }
    }

    private async Task RefreshConnectionsAsync(Guid id, CancellationToken cancellationToken, bool reconnect = false)
    {
        try
        {
            // Only the changed registration reconnects; every other server keeps its live session.
            if (reconnect)
            {
                await _connectionManager.ReconnectAsync(id, cancellationToken);
            }
            else
            {
                await _connectionManager.RefreshAsync(id, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or ObjectDisposedException)
        {
            // A refresh failure must not fail the persisted CRUD mutation: the row is committed, and the startup connector and the next
            // mutation both re-reconcile. The filter mirrors McpServerStartupConnector, so a genuinely unexpected fault still surfaces.
            _logger.LogWarning(exception, "MCP connection refresh after a registration change failed; the change is persisted and will reconcile on the next refresh.");
        }
    }
}
