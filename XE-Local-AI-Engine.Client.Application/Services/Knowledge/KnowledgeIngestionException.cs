namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Signals a knowledge-ingestion step failure whose <see cref="Reason" /> is a fixed, content-free message safe to
///     persist as <c>knowledge_documents.failure_reason</c> and surface to the UI. The reason never contains chunk or
///     document text; it describes the failure category only (e.g. the embedding model being unavailable).
/// </summary>
public sealed class KnowledgeIngestionException : Exception
{
    public KnowledgeIngestionException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public KnowledgeIngestionException(string reason, Exception innerException)
        : base(reason, innerException)
    {
        Reason = reason;
    }

    /// <summary>Content-free, user-facing failure reason for the document.</summary>
    public string Reason { get; }
}
