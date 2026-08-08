namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     A single curated starter-pack persona, transformed once at build time from the vendored agency-agents source and
///     shipped as an embedded JSON resource. <see cref="Instructions" /> is seeded verbatim as the imported agent's
///     system prompt; <see cref="OriginalTools" /> is informational only (the upstream tool names, dropped on import —
///     imported agents land with no tools).
/// </summary>
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
///     Read-only catalog of the curated starter-pack templates. Loads the embedded <c>agent-templates.seed.json</c>
///     resource once and serves it from memory — zero runtime network egress. Registered as a singleton because the
///     catalog is immutable and read-once.
/// </summary>
public interface IAgentTemplateCatalog
{
    /// <summary>Returns every template in the catalog, in seed-file order.</summary>
    IReadOnlyList<AgentTemplate> List();

    /// <summary>Returns the template for <paramref name="slug" />, or <c>null</c> when no template has that slug.</summary>
    AgentTemplate? TryGet(string slug);
}
