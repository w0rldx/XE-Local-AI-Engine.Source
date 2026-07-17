namespace XE_Local_AI_Engine.Client.Services.Knowledge.Tools;

/// <summary>
///     Worker-side name / description / parameter-schema constants for the read-only knowledge-base agent tools
///     (<c>search_knowledge_base</c>, <c>read_document</c>, <c>read_surrounding_chunks</c>). Each handler advertises its
///     model-visible schema from here and the offer provider merges the same descriptors into the loopback offer, so the
///     schema the model is offered can never drift from what the handler validates. The schemas are advisory to the
///     model; the handlers' own validation is authoritative. All three are read-only node-local retrieval surfaces, so
///     they auto-execute (no per-call approval) and are gated by <c>KnowledgeBase:AgentToolsEnabled</c>.
/// </summary>
internal static class SearchKnowledgeBaseToolDefinition
{
    public const string ToolName = "search_knowledge_base";

    public const string Description =
        "Search the node-local knowledge base (the operator's own uploaded documents) for passages relevant to a "
        + "question, and use ONLY the returned passages to ground a document-specific answer. Prefer this tool whenever "
        + "the question is about the operator's documents or local knowledge. Answering policy: rely solely on the "
        + "retrieved passages for document-grounded claims; do not invent facts or fill gaps from prior knowledge; if the "
        + "results do not contain enough information to answer, say so plainly instead of guessing. Typical flow: search "
        + "first, then read_surrounding_chunks around a promising hit for more context, or read_document to read a whole "
        + "document. Returns compact JSON hits with documentId, chunkId, title, section, content, source, score, and "
        + "chunkIndex; an empty result set means the knowledge base has nothing matching the query.";

    /// <summary>Query is required; limit defaults to 5 and is clamped to 1-20; optional single-document scope + neighbor expansion.</summary>
    public static readonly string ParameterSchema = $$"""
                                                      {
                                                        "type": "object",
                                                        "additionalProperties": false,
                                                        "required": ["query"],
                                                        "properties": {
                                                          "query": { "type": "string", "minLength": 1, "maxLength": {{KnowledgeQueryLimits.MaxQueryLength}} },
                                                          "limit": { "type": "integer", "minimum": 1, "maximum": 20 },
                                                          "documentId": { "type": "string", "maxLength": 64 },
                                                          "expandNeighbors": { "type": "boolean" }
                                                        }
                                                      }
                                                      """;
}

/// <summary>Name / description / schema constants for the <c>read_document</c> tool.</summary>
internal static class ReadDocumentToolDefinition
{
    public const string ToolName = "read_document";

    public const string Description =
        "Read a single knowledge-base document end to end by its documentId (usually obtained from a "
        + "search_knowledge_base hit). Returns the document's non-sensitive metadata plus its ordered chunks. The content "
        + "is bounded: a very large document is truncated and the result flags that truncation, so read the specific "
        + "sections you need rather than assuming the whole document is present. Use the returned passages only to ground "
        + "document-specific claims; do not invent content that is not present.";

    /// <summary>documentId is the only required argument.</summary>
    public const string ParameterSchema = """
                                          {
                                            "type": "object",
                                            "additionalProperties": false,
                                            "required": ["documentId"],
                                            "properties": {
                                              "documentId": { "type": "string", "minLength": 1, "maxLength": 64 }
                                            }
                                          }
                                          """;
}

/// <summary>Name / description / schema constants for the <c>read_surrounding_chunks</c> tool.</summary>
internal static class ReadSurroundingChunksToolDefinition
{
    public const string ToolName = "read_surrounding_chunks";

    public const string Description =
        "Read the chunks surrounding a specific chunk within a document, to recover context that straddles a chunk "
        + "boundary. Identify the target by its documentId and the chunkIndex of a search_knowledge_base hit, and request "
        + "how many chunks to include before and after it. Returns the neighbor window in document order. Use the returned "
        + "passages only to ground document-specific claims.";

    /// <summary>documentId + chunkIndex identify the anchor; before/after default to 1 and are clamped to at most 5 each.</summary>
    public const string ParameterSchema = """
                                          {
                                            "type": "object",
                                            "additionalProperties": false,
                                            "required": ["documentId", "chunkIndex"],
                                            "properties": {
                                              "documentId": { "type": "string", "minLength": 1, "maxLength": 64 },
                                              "chunkIndex": { "type": "integer", "minimum": 0 },
                                              "before": { "type": "integer", "minimum": 0, "maximum": 5 },
                                              "after": { "type": "integer", "minimum": 0, "maximum": 5 }
                                            }
                                          }
                                          """;
}

/// <summary>
///     Offer-side metadata for a single knowledge-base tool. Mirrors the shape the offer provider needs (name + schema +
///     approval flag); <see cref="RequiresApproval" /> is always <see langword="false" /> for these read-only tools.
/// </summary>
internal sealed record KnowledgeToolDescriptor(string Name, string Description, string ParameterSchema)
{
    /// <summary>Knowledge-base read tools never require approval.</summary>
    public bool RequiresApproval { get; }
}

/// <summary>
///     The model-visible descriptors for the three knowledge-base tools — name + schema + approval flag. The offer
///     provider consumes these to merge the tools into the capability-gated loopback offer, exactly like the coder tools.
/// </summary>
internal static class KnowledgeToolCatalog
{
    public static IReadOnlyList<KnowledgeToolDescriptor> Descriptors { get; } =
    [
        new KnowledgeToolDescriptor(SearchKnowledgeBaseToolDefinition.ToolName, SearchKnowledgeBaseToolDefinition.Description, SearchKnowledgeBaseToolDefinition.ParameterSchema),
        new KnowledgeToolDescriptor(ReadDocumentToolDefinition.ToolName, ReadDocumentToolDefinition.Description, ReadDocumentToolDefinition.ParameterSchema),
        new KnowledgeToolDescriptor(ReadSurroundingChunksToolDefinition.ToolName, ReadSurroundingChunksToolDefinition.Description, ReadSurroundingChunksToolDefinition.ParameterSchema)
    ];
}
