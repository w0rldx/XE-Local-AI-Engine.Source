namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class McpServerService : IMcpServerService
{
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
            Environment = RestoreMaskedEnvironment(input.Environment, existing.Environment),
            Enabled = existing.Enabled
        };

        McpServerRecord? updated;
        try
        {
            updated = await _store.UpdateAsync(id, edit, cancellationToken);
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
            await RefreshConnectionsAsync(cancellationToken);
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
            // No change to the enabled set, so no refresh — return the unchanged record.
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
        await RefreshConnectionsAsync(cancellationToken);

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
            await RefreshConnectionsAsync(cancellationToken);
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
    ///     already stored under that key.
    /// </summary>
    /// <remarks>
    ///     A key that carries the mask with no stored value keeps the mask verbatim: it is a new key whose value the
    ///     caller genuinely typed, and inventing an empty string for it would be a guess.
    /// </remarks>
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

    private async Task RefreshConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connectionManager.RefreshAsync(cancellationToken);
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
