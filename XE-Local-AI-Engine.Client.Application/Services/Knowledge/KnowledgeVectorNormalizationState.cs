namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IKnowledgeVectorNormalizationState" />. A single volatile flag: the search reads it on the hot
///     path (a plain volatile read, no lock) and the backfill service sets it once. Registered as a singleton so the
///     scoped search instances of a request all observe the same process-wide latch.
/// </summary>
public sealed class KnowledgeVectorNormalizationState : IKnowledgeVectorNormalizationState
{
    private volatile bool _isComplete;

    public bool IsComplete => _isComplete;

    public void MarkComplete()
    {
        _isComplete = true;
    }
}
