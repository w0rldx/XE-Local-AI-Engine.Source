namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     A single curated starter-pack persona, transformed once at build time from the vendored source and shipped as
///     an embedded JSON resource.
/// </summary>
/// <remarks>
///     <see cref="Instructions" /> is seeded verbatim as the imported agent's system prompt.
///     <see cref="OriginalTools" /> is informational only — the upstream tool names are dropped on import, so an
///     imported agent lands with no tools.
/// </remarks>
public sealed record AgentTemplate(
    string Slug,
    string Name,
    string? Description,
    string Division,
    string Instructions,
    int EstimatedPromptTokens,
    IReadOnlyList<string> OriginalTools,
    string SourceFile);

/// <summary>
///     Read-only catalog of the curated starter-pack templates.
/// </summary>
/// <remarks>
///     It loads the embedded <c>agent-templates.seed.json</c> resource once and serves it from memory, with zero
///     runtime network egress. A singleton, the catalog being immutable and read-once.
/// </remarks>
public interface IAgentTemplateCatalog
{
    /// <summary>Returns every template in the catalog, in seed-file order.</summary>
    IReadOnlyList<AgentTemplate> List();

    /// <summary>Returns the template for <paramref name="slug" />, or <c>null</c> when no template has that slug.</summary>
    AgentTemplate? TryGet(string slug);
}
