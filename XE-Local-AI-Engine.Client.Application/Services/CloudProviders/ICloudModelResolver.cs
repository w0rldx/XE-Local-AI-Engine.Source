namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Answers "is this model id served by a cloud provider?" from the stored cloud configuration. Every read is
///     BEST-EFFORT: a failure resolving the encrypted config is swallowed here (logged once, in one place) and reported
///     as "not cloud" / "no connection", so a caller's local-routing path still runs. Cancellation is never swallowed.
/// </summary>
public interface ICloudModelResolver
{
    /// <summary>
    ///     True when the model id is a cloud model — a Codex catalog id or a stored Azure Foundry deployment name.
    ///     A blank id is never a cloud model.
    /// </summary>
    Task<bool> IsCloudModelAsync(string? modelName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     True when the model id matches one of the stored Azure Foundry connection's deployment names (ordinal,
    ///     case-insensitive). Codex ids are NOT considered here — see <see cref="IsCloudModelAsync" />.
    /// </summary>
    Task<bool> IsAzureFoundryDeploymentAsync(string? modelName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The stored Azure Foundry connection, or <see langword="null" /> when none is configured (or the config
    ///     could not be read).
    /// </summary>
    Task<StoredAzureFoundryConnection?> ResolveAzureFoundryConnectionAsync(CancellationToken cancellationToken = default);
}
