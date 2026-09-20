namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     Persists the last successfully-fetched-and-validated remote catalog so a node that starts with a configured
///     <see cref="ModelCatalogOptions.RefreshUrl" /> but no network serves it instead of silently regressing to the
///     (potentially much older) bundled seed.
/// </summary>
/// <remarks>
///     Mirrors <c>INodeSettingsStore</c>'s tiny-local-JSON-file persistence pattern, but in its own file rather than
///     the shared node settings file: this is a raw catalog document, not a settings key.
/// </remarks>
public interface IModelCatalogCacheStore
{
    /// <summary>Loads the persisted last-good remote catalog, or <see langword="null" /> when none has ever been saved / it could not be read.</summary>
    Task<StoredModelCatalogCache?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists <paramref name="cache" />, overwriting any previously-saved copy.</summary>
    Task SaveAsync(StoredModelCatalogCache cache, CancellationToken cancellationToken = default);
}

/// <summary>The raw JSON of a successfully-validated remote catalog fetch, plus its provenance.</summary>
public sealed record StoredModelCatalogCache(string RawJson, DateTimeOffset FetchedAtUtc, string SourceUrl);
