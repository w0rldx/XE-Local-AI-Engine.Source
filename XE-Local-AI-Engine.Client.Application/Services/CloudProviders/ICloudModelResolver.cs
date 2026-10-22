namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Answers "is this model id served by a cloud provider?" from the stored cloud configuration.
/// </summary>
/// <remarks>
///     Every read is BEST-EFFORT: a failure resolving the encrypted config is swallowed here (logged once, in one
///     place) and reported as "not cloud" / "no connection", so a caller's local-routing path still runs.
///     Cancellation is never swallowed.
/// </remarks>
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

    /// <summary>
    ///     The declared context window of a stored Azure Foundry deployment or a Codex id with a sourced window, or
    ///     <see langword="null" /> when the id is neither (including when the config could not be read).
    /// </summary>
    /// <remarks>
    ///     The single home for "how big is this cloud model's window": the model-details route and the turn budget both read
    ///     it, so the chat meter and the budgeter cannot disagree. A deployment is matched before a Codex id, as routing does.
    /// </remarks>
    Task<CloudModelContextWindow?> ResolveContextWindowAsync(string? modelName, CancellationToken cancellationToken = default);
}
