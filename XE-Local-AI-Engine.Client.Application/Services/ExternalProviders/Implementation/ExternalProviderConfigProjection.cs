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
    public static (IReadOnlyList<ExternalProviderModelRegistration> Registrations, IReadOnlyDictionary<string, string> KeysByConnectionId) Project(StoredExternalProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var registrations = new List<ExternalProviderModelRegistration>();
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
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

            registrations.AddRange(connection.Models.Select(model =>
                new ExternalProviderModelRegistration(descriptor, ExternalProviderStore.ToDescriptor(model))));
        }

        return (registrations, keys);
    }
}
