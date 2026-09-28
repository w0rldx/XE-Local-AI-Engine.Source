namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Which pairs a cohort should hold, and which runs it had to leave out.</summary>
public sealed class BenchmarkPairwisePlan
{
    public required IReadOnlyList<BenchmarkPairwiseSlot> Slots { get; init; }

    public required IReadOnlyList<Guid> PairedRunIds { get; init; }

    public required IReadOnlyList<Guid> CappedRunIds { get; init; }
}
