namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-local read boundary that reconstructs golden-conversation harvest candidates from an agent's thumbs-up
///     assistant turns.
/// </summary>
/// <remarks>
///     The thumbs-up scan runs over plaintext columns (rating and ids) via parameterized raw ADO; the turn content is
///     read through <c>NodeChatDbContext</c> so the materialization interceptor decrypts it. No content is ever logged.
/// </remarks>
public interface IGoldenHarvestSourceStore
{
    /// <summary>
    ///     Returns the harvest candidate sources for <paramref name="agentDefinitionId" />: the most-recent thumbs-up
    ///     assistant turns (up to <paramref name="maxScan" />) with their decrypted lead-up turns and answer text.
    /// </summary>
    /// <remarks>
    ///     Purged conversations are excluded, and sources whose target message is missing are skipped.
    /// </remarks>
    Task<IReadOnlyList<HarvestCandidateSource>> ListThumbsUpSourcesAsync(Guid agentDefinitionId, int maxScan, CancellationToken cancellationToken = default);
}

/// <summary>One reconstructed conversation turn: a role (<c>"user"</c>/<c>"assistant"</c>) and its decrypted text.</summary>
public sealed class HarvestTurn
{
    public required string Role { get; init; }

    public required string Text { get; init; }
}

/// <summary>
///     A harvest candidate built from a single thumbs-up assistant message: the lead-up <see cref="PriorTurns" /> and the
///     operator-approved <see cref="ApprovedAnswerText" />, plus provenance ids for dedup and review.
/// </summary>
public sealed class HarvestCandidateSource
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string? ConversationTitle { get; init; }

    public required IReadOnlyList<HarvestTurn> PriorTurns { get; init; }

    public required string ApprovedAnswerText { get; init; }
}
