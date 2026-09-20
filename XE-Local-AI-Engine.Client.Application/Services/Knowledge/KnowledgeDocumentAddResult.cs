namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>Outcome of persisting a knowledge document.</summary>
/// <remarks>
///     Uploads dedupe by content hash. Repository documents instead retain a stable identity by collection + source kind
///     + source id + normalized source path: unchanged bytes return neither flag, while changed bytes update the
///     existing document and blob and set <see cref="WasUpdated" /> so it is reindexed.
/// </remarks>
public sealed class KnowledgeDocumentAddResult
{
    public required Guid DocumentId { get; init; }

    public required bool WasInserted { get; init; }

    public bool WasUpdated { get; init; }
}
