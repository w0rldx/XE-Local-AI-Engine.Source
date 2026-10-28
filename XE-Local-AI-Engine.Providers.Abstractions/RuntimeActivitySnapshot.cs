namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>A process-wide snapshot of a native runtime's activity that can conflict with runtime mutation.</summary>
public sealed class RuntimeActivitySnapshot
{
    public required int ActiveJobCount { get; init; }

    public required int SpawnReadinessCount { get; init; }

    public required int ResidentProcessCount { get; init; }

    public required bool MutationReserved { get; init; }

    public required bool EvictionReserved { get; init; }

    /// <summary>Whether anything at all is holding the runtime; the single flag the 409 envelope reports.</summary>
    public bool IsBusy =>
        MutationReserved
        || EvictionReserved
        || ActiveJobCount > 0
        || SpawnReadinessCount > 0
        || ResidentProcessCount > 0;
}
