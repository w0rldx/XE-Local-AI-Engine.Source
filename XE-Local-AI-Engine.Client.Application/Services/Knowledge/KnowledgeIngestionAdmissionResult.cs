namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Outcome of an admission: the document's resolved status — the value the upload response reports — and the
///     dispatcher's answer.
/// </summary>
/// <remarks>
///     The dispatcher's answer is <see langword="null" /> when the rule decided not to enqueue at all. The importer
///     counts an <see cref="KnowledgeIngestionEnqueueResult.Accepted" /> answer; both callers treat
///     <see cref="QueueFull" /> as a retryable busy condition.
/// </remarks>
public sealed class KnowledgeIngestionAdmissionResult
{
    public required KnowledgeDocumentStatus Status { get; init; }

    public required KnowledgeIngestionEnqueueResult? Enqueue { get; init; }

    /// <summary>Whether the bounded ingestion queue rejected this admission.</summary>
    public bool QueueFull => Enqueue is KnowledgeIngestionEnqueueResult.QueueFull;
}
