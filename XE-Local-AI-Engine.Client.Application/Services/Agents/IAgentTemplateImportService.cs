namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Outcome of a starter-pack import request. Each requested slug lands in exactly one bucket:
///     <see cref="Imported" /> (a new seeded row was created), <see cref="SkippedExisting" /> (already seeded, left
///     untouched), or <see cref="Unknown" /> (not in the catalog, no row written).
/// </summary>
public sealed class AgentTemplateImportResult
{
    public required IReadOnlyList<string> Imported { get; init; }

    public required IReadOnlyList<string> SkippedExisting { get; init; }

    public required IReadOnlyList<string> Unknown { get; init; }
}

/// <summary>
///     Idempotent, additive importer for the curated starter-pack templates. Maps each requested template to an
///     ordinary chat-persona agent definition (no tools, <c>Kind=Single</c>) and persists it through the forge-proof
///     seeded store path. Re-importing an already-seeded slug never duplicates a row.
/// </summary>
public interface IAgentTemplateImportService
{
    /// <summary>
    ///     Imports the requested <paramref name="slugs" /> (deduped). Slugs absent from the catalog are reported as
    ///     <c>Unknown</c>; slugs already seeded are reported as <c>SkippedExisting</c>; the rest are created as seeded
    ///     agent definitions and reported as <c>Imported</c>.
    /// </summary>
    Task<AgentTemplateImportResult> ImportAsync(IReadOnlyList<string> slugs, CancellationToken cancellationToken = default);
}
