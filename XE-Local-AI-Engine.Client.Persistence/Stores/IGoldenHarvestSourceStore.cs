namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-local read boundary that reconstructs golden-conversation harvest candidates from an agent's thumbs-up
///     assistant turns. The thumbs-up scan runs over plaintext columns (rating/ids) via parameterized raw ADO; the turn
///     content is read through <c>NodeChatDbContext</c> so the materialization interceptor decrypts it. No content is
///     ever logged.
/// </summary>
public interface IGoldenHarvestSourceStore
{
    /// <summary>
    ///     Returns the harvest candidate sources for <paramref name="agentDefinitionId" /> — the most-recent
    ///     thumbs-up assistant turns (up to <paramref name="maxScan" />) with their decrypted lead-up turns and the
    ///     approved answer text. Purged conversations are excluded. Sources whose target message is missing are skipped.
    /// </summary>
    Task<IReadOnlyList<HarvestCandidateSource>> ListThumbsUpSourcesAsync(Guid agentDefinitionId, int maxScan, CancellationToken cancellationToken = default);
}

/// <summary>One reconstructed conversation turn: a role (<c>"user"</c>/<c>"assistant"</c>) and its decrypted text.</summary>
public sealed record HarvestTurn(string Role, string Text);

/// <summary>
///     A harvest candidate built from a single thumbs-up assistant message: the lead-up <see cref="PriorTurns" /> and the
///     operator-approved <see cref="ApprovedAnswerText" />, plus provenance ids for dedup and review.
/// </summary>
public sealed record HarvestCandidateSource(
    Guid MessageId,
    Guid ConversationId,
    string? ConversationTitle,
    IReadOnlyList<HarvestTurn> PriorTurns,
    string ApprovedAnswerText);
