namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The ONE place a <see cref="StoredExternalProviderConfig" /> becomes registrations and connection keys.
/// </summary>
/// <remarks>
///     It is shared rather than duplicated because two callers must agree on the answer for exactly the same
///     configuration: the registry projects the cached read model the node routes from, and the reconciler — which
///     DELETES every <c>ext:</c> map row, allow-list entry and node default the configuration does not list — projects
///     the authoritative load it took itself. Reading the second answer back through the registry instead would let an
///     unreadable store surface as an empty registration list, and an empty list is a mandate to erase everything.
/// </remarks>
internal static class ExternalProviderConfigProjection
{
    /// <summary>
    ///     Projects <paramref name="config" /> onto the ordered registrations and the per-connection keys.
    /// </summary>
    /// <remarks>
    ///     A stored connection whose base URL no longer parses is DROPPED rather than allowed to fault every lookup:
    ///     one hand-edited connection must not take the operator's other connections offline with it. Its models then
    ///     resolve to null, which every consumer already treats as fail-closed.
    /// </remarks>
    public static Projection Project(StoredExternalProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var registrations = new List<ExternalProviderModelRegistration>();
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = new Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>>(StringComparer.Ordinal);
        foreach (var connection in config.Connections)
        {
            ExternalProviderConnectionDescriptor descriptor;
            try
            {
                descriptor = ExternalProviderStore.ToDescriptor(connection);
            }
            catch (Exception exception) when (exception is UriFormatException or ArgumentException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(connection.ApiKey))
            {
                keys[descriptor.Id] = connection.ApiKey;
            }

            if (connection.Headers.Count > 0)
            {
                headers[descriptor.Id] = [.. connection.Headers.Select(static header => new KeyValuePair<string, string>(header.Name, header.Value ?? string.Empty))];
            }

            registrations.AddRange(connection.Models.Select(model =>
                new ExternalProviderModelRegistration
                {
                    Connection = descriptor,
                    Model = ExternalProviderStore.ToDescriptor(model)
                }));
        }

        return new Projection
        {
            Registrations = registrations,
            KeysByConnectionId = keys,
            HeadersByConnectionId = headers
        };
    }

    /// <summary>The projected registrations, API keys and custom headers; a class, so no generated ToString can print a secret.</summary>
    public sealed class Projection
    {
        public required IReadOnlyList<ExternalProviderModelRegistration> Registrations { get; init; }

        public required IReadOnlyDictionary<string, string> KeysByConnectionId { get; init; }

        public required IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, string>>> HeadersByConnectionId { get; init; }
    }
}
