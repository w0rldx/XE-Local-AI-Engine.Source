namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What <see cref="IInferenceProfileStore.MarkFrozenAsync" /> did, one case per way the freeze gate can answer.</summary>
public abstract record InferenceProfileFreezeResult
{
    /// <summary>The row moved to <c>Frozen</c>; <see cref="Profile" /> is the row as stored.</summary>
    public sealed record Frozen : InferenceProfileFreezeResult
    {
        public required InferenceProfileRecord Profile { get; init; }
    }

    /// <summary>No row has the id: it was removed after the caller read it.</summary>
    public sealed record NotFound : InferenceProfileFreezeResult;

    /// <summary>The row exists but is not <c>Explored</c> at write time, so the freeze gate left it untouched.</summary>
    public sealed record NotExplored : InferenceProfileFreezeResult;
}
