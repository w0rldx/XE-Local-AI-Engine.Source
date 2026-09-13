namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

using System.Text;

/// <summary>
///     Persists the last successfully fetched-and-validated remote catalog so it survives a restart: when the node
///     starts with a configured <see cref="ExternalAppCatalogOptions.RefreshUrl" /> but the network is unreachable,
///     the provider serves this instead of regressing to the (potentially much older) bundled seed. A small JSON file
///     under the node data directory, not a database row — the catalog is a cached document, not node state.
/// </summary>
public interface IExternalAppCatalogCacheStore
{
    /// <summary>Loads the persisted last-good catalog, or <see langword="null" /> when none has ever been saved or it could not be read.</summary>
    Task<StoredExternalAppCatalogCache?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists <paramref name="cache" />, overwriting any previously saved copy.</summary>
    Task SaveAsync(StoredExternalAppCatalogCache cache, CancellationToken cancellationToken = default);
}

/// <summary>
///     The raw JSON of a successfully validated remote catalog fetch, plus its provenance.
///     <see cref="SourceUrl" /> binds the copy — and the <see cref="ETag" /> revalidation token — to the origin it
///     came from: a cache written for one catalog URL is ignored (and overwritten on the next success) once the
///     configured URL changes, so switching catalog sources can never resurrect the old source's document.
/// </summary>
public sealed record StoredExternalAppCatalogCache(
    string RawJson,
    DateTimeOffset FetchedAtUtc,
    string SourceUrl,
    string? ETag)
{
    // The generated ToString() prints every property, and a manifest carries the whole authored catalog — variable
    // defaults, inlined file bodies, the raw fetched document. One LogDebug("{Snapshot}", snapshot) downstream would
    // therefore write the catalog into the node log. Suppressing the printer makes that impossible rather than merely
    // forbidden, and ExternalAppCatalogRecordPrintingTests asserts no manifest content can appear.
    //
    // `private bool PrintMembers(StringBuilder)` and never `protected override`: on a sealed record whose base is
    // object the compiler expects exactly this signature, and the override form does not compile here.
    //
    // The compiler's shape is also why four analyzers have to be silenced rather than obeyed: the printer is unused,
    // instance-free and unimplemented BY DESIGN, and every fix they suggest — making it static, dropping the
    // parameter — changes the signature into one the compiler no longer recognises as the record's printer, which
    // silently restores the ToString() that prints the catalog.
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}
