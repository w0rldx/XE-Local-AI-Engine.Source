namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Persists a document's sections, chunks (plaintext — the FTS index is maintained by triggers), and vectors, then
///     advances the document row to <c>Indexed</c>, all in one transaction over the raw-SQL path. Rechecks the document
///     still exists before writing so a delete that races ingestion never re-inserts rows for a removed document.
/// </summary>
public interface IKnowledgeIndexWriter
{
    /// <summary>
    ///     Writes the projection for one document atomically. Returns <see langword="true" /> when the document was
    ///     written and marked <c>Indexed</c>; <see langword="false" /> when the document no longer exists (delete race),
    ///     in which case nothing is written.
    /// </summary>
    Task<bool> WriteAsync(KnowledgeIndexInput input, CancellationToken cancellationToken);
}
