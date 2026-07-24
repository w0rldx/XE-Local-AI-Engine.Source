namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     The canonical on-disk shape (schema v2) for stored cloud provider configuration.
/// </summary>
/// <remarks>
///     No <c>required</c> members so a partial / legacy JSON parse never throws into the destructive
///     deserialization catch. The <see cref="SchemaVersion" /> keeps future additions (e.g. a
///     per-model catalog <c>Kind</c>) additive.
/// </remarks>
public sealed record StoredCloudProviderConfig
{
    /// <summary>
    ///     The storage schema version. Current canonical version is 2.
    /// </summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>
    ///     The cloud provider name (currently only Azure Foundry).
    /// </summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>
    ///     The Azure Foundry connection, or null when no Azure connection is configured.
    /// </summary>
    public StoredAzureFoundryConnection? AzureFoundry { get; init; }
}
