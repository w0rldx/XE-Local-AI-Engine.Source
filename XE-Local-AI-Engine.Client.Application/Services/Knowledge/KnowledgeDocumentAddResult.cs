namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Outcome of persisting a knowledge document. Uploads dedupe by content hash. Repository documents instead retain a
///     stable identity by collection + source kind + source id + normalized source path: unchanged bytes return neither
///     flag, while
///     changed bytes update the existing document/blob and set <paramref name="WasUpdated" /> so it is reindexed.
/// </summary>
public sealed record KnowledgeDocumentAddResult(Guid DocumentId, bool WasInserted, bool WasUpdated = false);
