namespace XE_Local_AI_Engine.Client.Services.Knowledge.Tools;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Worker-side name, description and parameter-schema constants for the read-only knowledge-base agent tools
///     (<c>search_knowledge_base</c>, <c>read_document</c>, <c>read_surrounding_chunks</c>).
/// </summary>
/// <remarks>
///     Each handler advertises its model-visible schema from here and the offer provider merges the same descriptors
///     into the loopback offer, so the schema the model is offered can never drift from what the handler validates. The
///     schemas are advisory to the model; the handlers' own validation is authoritative. All three are read-only
///     node-local retrieval surfaces, so they auto-execute with no per-call approval, gated by
///     <c>KnowledgeBase:AgentToolsEnabled</c>.
/// </remarks>
internal static class SearchKnowledgeBaseToolDefinition
{
    public const string ToolName = "search_knowledge_base";

    public const string Description =
        "Search the node-local knowledge base (the operator's own uploaded documents) for passages relevant to a "
        + "question about those documents. Prefer this tool whenever the question is about the operator's documents or "
        + "local knowledge; a general question does not need it. Answering policy for questions about the operator's "
        + "documents: rely solely on the retrieved passages for document-grounded claims; do not invent what the documents "
        + "say; if the results do not contain enough information to answer, say so plainly instead of guessing. Questions "
        + "that are not about the operator's documents are answered as usual, from general knowledge. Typical flow: search "
        + "first (omit limit to get the node's default page of hits) and start here for any question about the documents; "
        + "then pass the returned collectionId and chunkIndex to read_surrounding_chunks with a small window around the best "
        + "hit. Use read_document only for a short document or to page deliberately through a long one with offset. Returns "
        + "compact JSON hits with collectionId, documentId, chunkId, content, score, "
        + "scoreKind (Fusion or Rerank: the scale of score, comparable only within one result, not a probability), and "
        + "chunkIndex; an empty result set means the knowledge base has nothing matching the query.";

    /// <summary>
    ///     Query is required; limit defaults to the node's <c>KnowledgeSearchDefaultResults</c> and is clamped to its
    ///     <c>KnowledgeSearchMaxResults</c>, whose bound is this schema's static maximum of 20; optional single-document scope
    ///     + neighbor expansion.
    /// </summary>
    public static readonly string ParameterSchema = $$"""
                                                      {
                                                        "type": "object",
                                                        "additionalProperties": false,
                                                        "required": ["query"],
                                                        "properties": {
                                                          "query": { "type": "string", "minLength": 1, "maxLength": {{KnowledgeQueryLimits.MaxQueryLength}} },
                                                          "limit": {
                                                            "type": "integer",
                                                            "minimum": 1,
                                                            "maximum": 20,
                                                            "description": "Optional. Omit it to get the node's default page of hits ({{StoredNodeSettings.DefaultKnowledgeSearchDefaultResults}} unless the operator changed it). Raise it only when that first page was not enough, because every extra hit costs context."
                                                          },
                                                          "documentId": { "type": "string", "maxLength": 64 },
                                                          "collectionId": { "type": "string", "minLength": 1, "maxLength": {{KnowledgeCollectionScope.MaxLength}} },
                                                          "expandNeighbors": { "type": "boolean" }
                                                        }
                                                      }
                                                      """;
}

/// <summary>Name / description / schema constants for the <c>read_document</c> tool.</summary>
internal static class ReadDocumentToolDefinition
{
    public const string ToolName = "read_document";

    /// <summary>Chunks returned when the model omits <c>limit</c>.</summary>
    public const int DefaultLimit = 6;

    /// <summary>Ceiling on chunks per call, so one call cannot pull a whole book into context.</summary>
    public const int MaxLimit = 12;

    public const string Description =
        "Read one page of a knowledge-base document's ordered chunks by its documentId and collectionId (usually from a "
        + "search_knowledge_base hit). Use it only for a short document, or to page deliberately through a long one: pass "
        + "offset (default 0) and limit, then the returned nextOffset to continue; nextOffset is null at the end and "
        + "totalChunks gives the document length. For a specific question, search first and use read_surrounding_chunks "
        + "around the best hit instead. Use the returned passages only to ground document-specific claims; do not invent "
        + "content that is not present.";

    /// <summary>documentId and collectionId are required to preserve the collection namespace boundary; offset and limit page.</summary>
    public static readonly string ParameterSchema = $$"""
                                                      {
                                                        "type": "object",
                                                        "additionalProperties": false,
                                                        "required": ["documentId", "collectionId"],
                                                        "properties": {
                                                          "documentId": { "type": "string", "minLength": 1, "maxLength": 64 },
                                                          "collectionId": { "type": "string", "minLength": 1, "maxLength": {{KnowledgeCollectionScope.MaxLength}} },
                                                          "offset": { "type": "integer", "minimum": 0, "description": "Position of the first chunk to return; default 0. Pass the previous nextOffset to continue." },
                                                          "limit": { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}}, "description": "Chunks to return; default {{DefaultLimit}}." }
                                                        }
                                                      }
                                                      """;
}

/// <summary>Name / description / schema constants for the <c>read_surrounding_chunks</c> tool.</summary>
internal static class ReadSurroundingChunksToolDefinition
{
    public const string ToolName = "read_surrounding_chunks";

    /// <summary>Neighbors on each side when the model omits <c>before</c> or <c>after</c>.</summary>
    public const int DefaultNeighbors = 2;

    /// <summary>Ceiling on neighbors per side, so the widest window is seven chunks.</summary>
    public const int MaxNeighbors = 3;

    public const string Description =
        "Read the chunks surrounding a specific chunk within a document, to recover context that straddles a chunk "
        + "boundary. Identify the target by its documentId, collectionId, and the chunkIndex of a search_knowledge_base hit. "
        + "Use a small window: before and after default to 2; widen only when the passage is still cut off. Returns the "
        + "neighbor window in document order. Use the returned passages only to ground document-specific claims.";

    /// <summary>documentId + collectionId + chunkIndex identify the anchor; before/after default to 2 and are clamped to at most 3 each.</summary>
    public static readonly string ParameterSchema = $$"""
                                                      {
                                                        "type": "object",
                                                        "additionalProperties": false,
                                                        "required": ["documentId", "collectionId", "chunkIndex"],
                                                        "properties": {
                                                          "documentId": { "type": "string", "minLength": 1, "maxLength": 64 },
                                                          "collectionId": { "type": "string", "minLength": 1, "maxLength": {{KnowledgeCollectionScope.MaxLength}} },
                                                          "chunkIndex": { "type": "integer", "minimum": 0 },
                                                          "before": { "type": "integer", "minimum": 0, "maximum": {{MaxNeighbors}}, "description": "Chunks before the anchor; default {{DefaultNeighbors}}." },
                                                          "after": { "type": "integer", "minimum": 0, "maximum": {{MaxNeighbors}}, "description": "Chunks after the anchor; default {{DefaultNeighbors}}." }
                                                        }
                                                      }
                                                      """;
}

/// <summary>
///     Offer-side metadata for a single knowledge-base tool. Mirrors the shape the offer provider needs (name + schema +
///     approval flag + risk category); <see cref="RequiresApproval" /> is always <see langword="false" /> for these
///     read-only tools and <see cref="Category" /> is always <see cref="ToolCategory.ReadLocal" /> (read-only,
///     node-local retrieval).
/// </summary>
internal sealed class KnowledgeToolDescriptor
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string ParameterSchema { get; init; }

    /// <summary>Knowledge-base read tools never require approval.</summary>
    public bool RequiresApproval { get; }

    /// <summary>Knowledge-base read tools are read-only, node-local retrieval surfaces.</summary>
    public ToolCategory Category { get; } = ToolCategory.ReadLocal;
}

/// <summary>
///     The model-visible descriptors for the three knowledge-base tools — name + schema + approval flag. The offer
///     provider consumes these to merge the tools into the capability-gated loopback offer, exactly like the coder tools.
/// </summary>
internal static class KnowledgeToolCatalog
{
    public static IReadOnlyList<KnowledgeToolDescriptor> Descriptors { get; } =
    [
        new KnowledgeToolDescriptor
        {
            Name = SearchKnowledgeBaseToolDefinition.ToolName,
            Description = SearchKnowledgeBaseToolDefinition.Description,
            ParameterSchema = SearchKnowledgeBaseToolDefinition.ParameterSchema
        },
        new KnowledgeToolDescriptor
        {
            Name = ReadDocumentToolDefinition.ToolName,
            Description = ReadDocumentToolDefinition.Description,
            ParameterSchema = ReadDocumentToolDefinition.ParameterSchema
        },
        new KnowledgeToolDescriptor
        {
            Name = ReadSurroundingChunksToolDefinition.ToolName,
            Description = ReadSurroundingChunksToolDefinition.Description,
            ParameterSchema = ReadSurroundingChunksToolDefinition.ParameterSchema
        }
    ];
}
