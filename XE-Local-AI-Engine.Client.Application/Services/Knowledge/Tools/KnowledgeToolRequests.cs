namespace XE_Local_AI_Engine.Client.Services.Knowledge.Tools;

/// <summary>
///     Typed projection of the <c>search_knowledge_base</c> JSON arguments. The bridge stays JSON-in / JSON-out, so the
///     handler deserializes into this record and validates it before any retrieval call.
/// </summary>
internal sealed record SearchKnowledgeBaseToolRequest
{
    public string? Query { get; init; }

    public int? Limit { get; init; }

    public string? DocumentId { get; init; }

    public bool? ExpandNeighbors { get; init; }

    public string? CollectionId { get; init; }
}

/// <summary>Typed projection of the <c>read_document</c> JSON arguments.</summary>
internal sealed record ReadDocumentToolRequest
{
    public string? DocumentId { get; init; }

    public string? CollectionId { get; init; }
}

/// <summary>Typed projection of the <c>read_surrounding_chunks</c> JSON arguments.</summary>
internal sealed record ReadSurroundingChunksToolRequest
{
    public string? DocumentId { get; init; }

    public string? CollectionId { get; init; }

    public int? ChunkIndex { get; init; }

    public int? Before { get; init; }

    public int? After { get; init; }
}
