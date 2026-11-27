namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

using System.Text;

/// <summary>The raw JSON of a successfully validated remote catalog fetch, plus its provenance.</summary>
/// <remarks>
///     <see cref="SourceUrl" /> binds the copy — and the <see cref="ETag" /> revalidation token — to the origin it
///     came from: a cache written for one catalog URL is ignored (and overwritten on the next success) once the
///     configured URL changes, so switching catalog sources can never resurrect the old source's document.
/// </remarks>
public sealed record StoredExternalAppCatalogCache
{
    public required string RawJson { get; init; }

    public required DateTimeOffset FetchedAtUtc { get; init; }

    public required string SourceUrl { get; init; }

    public required string? ETag { get; init; }

    /// <summary>Suppresses the record's generated <c>ToString()</c>, which would print the whole authored catalog.</summary>
    /// <remarks>
    ///     Variable defaults, inlined file bodies and the raw fetched document would reach the node log through one
    ///     downstream structured log of a snapshot; <c>ExternalAppCatalogRecordPrintingTests</c> asserts no manifest
    ///     content can appear. On a sealed record whose base is <c>object</c> the compiler recognizes only
    ///     <c>private bool PrintMembers(StringBuilder)</c>, never <c>protected override</c>, which is why CA1822,
    ///     S2325, S1172 and IDE0060 are suppressed: every fix they offer restores the printing <c>ToString()</c>.
    /// </remarks>
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}
