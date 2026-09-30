namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>The frozen membership: what trains, what is held back, and the held-back rows' canonical sequences.</summary>
internal sealed class TrainingSplit
{
    public required IReadOnlyList<Guid> Train { get; init; }

    public required IReadOnlyList<Guid> Holdout { get; init; }

    public required IReadOnlyList<int> HoldoutSequences { get; init; }
}
