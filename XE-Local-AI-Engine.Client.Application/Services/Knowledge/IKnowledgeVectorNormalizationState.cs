namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Process-wide latch recording whether every stored chunk vector is known to be L2-normalized — the precondition for
///     the managed cosine search to score with a plain dot product instead of full cosine similarity. New writes are
///     normalized at ingestion unconditionally; this latch covers the one-time backfill of legacy (pre-normalization)
///     rows. Until the backfill for this database has completed, the search stays on the scale-invariant cosine path,
///     which is correct whether or not a given stored row is normalized. Once completed the latch is set for the process
///     lifetime (it only ever transitions false → true), and every subsequent search takes the dot-product fast path.
/// </summary>
public interface IKnowledgeVectorNormalizationState
{
    /// <summary>
    ///     <see langword="true" /> once the legacy-vector normalization backfill has completed for this database, so all
    ///     stored vectors are unit length and the search may score with a dot product.
    /// </summary>
    bool IsComplete { get; }

    /// <summary>
    ///     Latches <see cref="IsComplete" /> to <see langword="true" />. Idempotent; only ever moves false → true. Called
    ///     by the backfill service after a completed pass (or immediately at startup when the durable marker shows a prior
    ///     run already finished).
    /// </summary>
    void MarkComplete();
}
