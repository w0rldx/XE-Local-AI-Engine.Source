namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Everything one step needs to know about its session, loaded once.
/// </summary>
internal sealed record WorkSessionState
{
    public required AgentWorkSessionSnapshot Session { get; init; }

    public required IReadOnlyList<WorkSessionTaskSnapshot> Tasks { get; init; }

    public required IReadOnlyList<WorkSessionFindingSnapshot> Findings { get; init; }

    public required IReadOnlyList<WorkSessionArtifactSnapshot> Artifacts { get; init; }

    public required WorkSessionCheckpointSnapshot? LastCheckpoint { get; init; }
}
