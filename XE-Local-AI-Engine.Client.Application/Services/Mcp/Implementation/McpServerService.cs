namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class McpServerService(
    IMcpServerStore store,
    IMcpServerConnectionManager connectionManager,
    IOptions<McpOptions> mcpOptions,
    ILogger<McpServerService> logger) : IMcpServerService
{
    private readonly IMcpServerConnectionManager _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
    private readonly ILogger<McpServerService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptions<McpOptions> _mcpOptions = mcpOptions ?? throw new ArgumentNullException(nameof(mcpOptions));
    private readonly IMcpServerStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<McpServerRecord> CreateAsync(McpServerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        Validate(input);
        input = NormalizeTrustTier(input);
        await EnsureNameAvailableAsync(input.Name, excludeId: null, cancellationToken).ConfigureAwait(false);

        try
        {
            // The store forces Enabled = false on create, so the enabled set is unchanged and no refresh is needed.
            return await _store.AddAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
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

        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        await EnsureNameAvailableAsync(input.Name, id, cancellationToken).ConfigureAwait(false);

        // A PUT edit never flips the enabled state — that is the dedicated SetEnabledAsync action — so carry the current
        // enabled flag through to the store regardless of what the request body claims. Environment values the caller
        // sent back masked are restored from the stored record: the API never returns a value, so a form that
        // round-trips what it was shown must not overwrite a secret with the placeholder it was shown instead.
        var edit = input with
        {
            Environment = RestoreMaskedEnvironment(input.Environment, existing.Environment),
            Enabled = existing.Enabled
        };

        McpServerRecord? updated;
        try
        {
            updated = await _store.UpdateAsync(id, edit, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            // The unique Name index is the backstop when a concurrent rename races past the pre-check above.
            throw new McpServerValidationException($"An MCP server named '{input.Name}' is already registered.", exception);
        }

        if (updated is null)
        {
            return null;
        }

        // Only an enabled server has a live connection that a config change can affect; a disabled server contributes no
        // tools either way, so editing it never needs a snapshot refresh.
        if (updated.Enabled)
        {
            await RefreshConnectionsAsync(cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<McpServerRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        if (existing.Enabled == enabled)
        {
            // No change to the enabled set, so no refresh — return the unchanged record.
            return existing;
        }

        // Flip only the enabled flag via the dedicated store method: it touches just the flag (single Version bump,
        // timestamp) and leaves the encrypted secret columns untouched, so a toggle never re-encrypts args/env/description.
        var updated = await _store.SetEnabledAsync(id, enabled, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return null;
        }

        // The enabled set changed (a server was connected or disconnected), so re-publish the live tool snapshot.
        await RefreshConnectionsAsync(cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        var deleted = await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return false;
        }

        // Removing an enabled server shrinks the connected set; a disabled server had no live connection to tear down.
        if (existing.Enabled)
        {
            await RefreshConnectionsAsync(cancellationToken).ConfigureAwait(false);
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

    public IReadOnlyList<McpServerConnectionStatus> GetConnectionStatuses()
    {
        return _connectionManager.GetStatuses();
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

        if (!Enum.IsDefined(input.TrustTier))
        {
            throw new McpServerValidationException($"Trust tier '{input.TrustTier}' is not a valid MCP trust tier.");
        }

        if (input.TrustTier == McpTrustTier.BuiltInTrusted)
        {
            // BuiltInTrusted names a transport the ENGINE owns. Nothing registered through this surface is one, and
            // accepting the value here would let an operator (or anything holding a session) label a third-party
            // executable as engine-owned. Refused rather than silently downgraded, so the attempt is visible.
            throw new McpServerValidationException("Trust tier 'BuiltInTrusted' is reserved for engine-owned MCP transports and cannot be assigned to a registration.");
        }

        switch (input.TransportKind)
        {
            case McpTransportKind.Stdio:
                if (string.IsNullOrWhiteSpace(input.Command))
                {
                    throw new McpServerValidationException("Command is required for a stdio MCP server.");
                }

                break;

            case McpTransportKind.Http:
                ValidateHttpUrl(input.Url);
                break;

            default:
                throw new McpServerValidationException($"Transport '{input.TransportKind}' is not supported.");
        }
    }

    /// <summary>
    ///     Replaces every environment value the caller sent as <see cref="McpEnvironmentMask.Value" /> with the value
    ///     already stored under that key. A key that carries the mask and has no stored value keeps the mask verbatim —
    ///     it is a new key whose value the caller genuinely typed, and inventing an empty string for it would be a
    ///     guess.
    /// </summary>
    private static IReadOnlyDictionary<string, string> RestoreMaskedEnvironment(IReadOnlyDictionary<string, string> incoming,
        IReadOnlyDictionary<string, string> stored)
    {
        var restored = new Dictionary<string, string>(incoming.Count, StringComparer.Ordinal);
        foreach (var (key, value) in incoming)
        {
            restored[key] = string.Equals(value, McpEnvironmentMask.Value, StringComparison.Ordinal)
                            && stored.TryGetValue(key, out var storedValue)
                ? storedValue
                : value;
        }

        return restored;
    }

    /// <summary>
    ///     The tier answers "where does this server's PROCESS run", so it is inert for HTTP — this node launches
    ///     nothing for an HTTP registration, it opens a loopback socket to a server that is already running. An HTTP
    ///     row is therefore stored at the column default rather than at whatever the request carried, so a persisted
    ///     <see cref="McpTrustTier.PrivilegedHost" /> can never be read as a host grant somebody actually made.
    /// </summary>
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

        // The HTTP transport is loopback-only by default (the connection manager re-checks at connect time as defence in
        // depth). Match the configured allow-list case-insensitively against the URL host so an operator-widened list and
        // the connect-time check agree. Uri.Host wraps an IPv6 literal in brackets (e.g. "[::1]"), so strip them before
        // the compare — the allow-list stores the bare address ("::1") — matching the connection manager's factory-side
        // normalization so both sides accept http://[::1]/.
        var host = uri.Host.Trim('[', ']');
        var loopbackHosts = _mcpOptions.Value.HttpLoopbackHosts ?? [];
        var hostAllowed = loopbackHosts.Any(allowed => string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase));
        if (!hostAllowed)
        {
            throw new McpServerValidationException($"Url host '{host}' is not in the allowed loopback set ({string.Join(", ", loopbackHosts)}).");
        }
    }

    private async Task EnsureNameAvailableAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        // Pre-check against the current registrations so the common case returns a friendly validation error; the unique
        // index is the backstop for a concurrent race (caught as a UniqueViolation by the callers). Name uniqueness is
        // case-insensitive because the qualified tool-name slug derives from it and collisions must be impossible.
        var existing = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var clash = existing.Any(record => record.Id != excludeId
                                           && string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash)
        {
            throw new McpServerValidationException($"An MCP server named '{name}' is already registered.");
        }
    }

    private async Task RefreshConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connectionManager.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or ObjectDisposedException)
        {
            // A refresh failure must not fail the persisted CRUD mutation: the row is already committed, the startup
            // connector and the next mutation both re-reconcile, and the connection manager isolates per-server failures
            // internally. Log and continue so the caller still sees its successful create/update/delete. The filter
            // mirrors McpServerStartupConnector (plus ObjectDisposedException, since the manager holds long-lived
            // clients) so a genuinely unexpected fault still surfaces rather than being silently swallowed.
            _logger.LogWarning(exception, "MCP connection refresh after a registration change failed; the change is persisted and will reconcile on the next refresh.");
        }
    }
}
