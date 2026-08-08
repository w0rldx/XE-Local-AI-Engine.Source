namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Outcome of persisting a knowledge document. <paramref name="WasInserted" /> is <c>false</c> when an identical file
///     (same content hash) already existed — in that case <paramref name="DocumentId" /> is the pre-existing document's id
///     and no new blob was written (content-hash dedupe, resolved via <c>INSERT … ON CONFLICT DO NOTHING</c> + re-select).
/// </summary>
public sealed record KnowledgeDocumentAddResult(Guid DocumentId, bool WasInserted);
