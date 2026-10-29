namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What a conditional provider-map write did: the row it stored, or the precondition no longer held.</summary>
/// <remarks>
///     Returned by <see cref="IModelProviderMapStore.TryInsertAsync" /> and <see cref="IModelProviderMapStore.TryUpdateAsync" />.
///     Neither tells a stale revision from a vanished row, because the single conditional statement cannot: the caller
///     re-reads under its lease when it needs to know.
/// </remarks>
public abstract record ModelProviderMapWriteResult
{
    /// <summary>The write landed; <see cref="Record" /> is the row as stored, with its new revision.</summary>
    public sealed record Written : ModelProviderMapWriteResult
    {
        public required ModelProviderMapRecord Record { get; init; }
    }

    /// <summary>
    ///     Nothing was written: an insert found the case-insensitive key already present, or an update found the row at
    ///     another revision or gone.
    /// </summary>
    public sealed record Superseded : ModelProviderMapWriteResult;
}
