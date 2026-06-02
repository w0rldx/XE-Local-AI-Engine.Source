namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

/// <summary>
///     Wire summary of a single curated starter-pack template. The full <c>Instructions</c> body is intentionally not
///     projected into the gallery list; <see cref="EstimatedPromptTokens" /> (a chars/4 heuristic) drives the size
///     warning, <see cref="HasOriginalTools" /> flags upstream tool references (dropped on import), and
///     <see cref="AlreadyImported" /> lets the gallery disable slugs that already exist.
/// </summary>
public sealed class AgentTemplateSummary
{
    public required string Slug { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Division { get; init; }

    public required int EstimatedPromptTokens { get; init; }

    public required bool HasOriginalTools { get; init; }

    public required bool AlreadyImported { get; init; }
}

public sealed class ListAgentTemplatesResponse
{
    public required IReadOnlyList<AgentTemplateSummary> Items { get; init; }
}

/// <summary>Import request: the catalog slugs the operator selected. A null/empty list imports nothing.</summary>
public sealed class ImportAgentTemplatesRequest
{
    public IReadOnlyList<string>? Slugs { get; init; }
}

/// <summary>
///     Import outcome buckets. Each requested slug lands in exactly one list: <see cref="Imported" /> (newly seeded),
///     <see cref="SkippedExisting" /> (already seeded), or <see cref="Unknown" /> (not in the catalog).
/// </summary>
public sealed class ImportAgentTemplatesResponse
{
    public required IReadOnlyList<string> Imported { get; init; }

    public required IReadOnlyList<string> SkippedExisting { get; init; }

    public required IReadOnlyList<string> Unknown { get; init; }
}
