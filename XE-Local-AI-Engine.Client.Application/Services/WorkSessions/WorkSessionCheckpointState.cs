namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The structured half of a checkpoint: what the session was doing, as ids rather than prose. The prose half is the
///     conversation synopsis, and it is nullable — a node with no local chat model produces none, and a placeholder
///     would be a lie.
/// </summary>
public sealed record WorkSessionCheckpointState(
    Guid? CurrentTaskId,
    IReadOnlyList<Guid> OpenTaskIds,
    IReadOnlyList<Guid> KeyFindingIds,
    string? NextAction,
    int Step);
