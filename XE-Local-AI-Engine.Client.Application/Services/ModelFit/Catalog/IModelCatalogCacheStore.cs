namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     Persists the last successfully-fetched-and-validated remote catalog so it survives a restart: when the node
///     starts with a configured <see cref="ModelCatalogOptions.RefreshUrl" /> but the network is unreachable, the
///     provider serves this instead of silently regressing to the (potentially much older) bundled seed. Mirrors
///     <c>INodeSettingsStore</c>'s tiny-local-JSON-file persistence pattern; a separate file (not the shared node
///     settings file) since this is a raw catalog document, not a settings key.
/// </summary>
public interface IModelCatalogCacheStore
{
    /// <summary>Loads the persisted last-good remote catalog, or <see langword="null" /> when none has ever been saved / it could not be read.</summary>
    Task<StoredModelCatalogCache?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists <paramref name="cache" />, overwriting any previously-saved copy.</summary>
    Task SaveAsync(StoredModelCatalogCache cache, CancellationToken cancellationToken = default);
}

/// <summary>The raw JSON of a successfully-validated remote catalog fetch, plus its provenance.</summary>
public sealed record StoredModelCatalogCache(string RawJson, DateTimeOffset FetchedAtUtc, string SourceUrl);
