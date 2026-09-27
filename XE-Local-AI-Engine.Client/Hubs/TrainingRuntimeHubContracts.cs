namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>Stable SignalR client-method name for training-runtime status pushes.</summary>
public static class TrainingRuntimeHubEvents
{
    public const string StatusChanged = "trainingRuntime.statusChanged";
}

/// <summary>
///     Stable SignalR wire shape. The provider contract stays transport-agnostic; this projection keeps its CLR types
///     from leaking onto the wire and can absorb new fields without changing the provider event contract.
/// </summary>
internal sealed class TrainingRuntimeStatusHubMessage
{
    public required string Phase { get; init; }
    public required IReadOnlyList<string> AppendedLogLines { get; init; }
    public required long AppendedLogStartSequence { get; init; }
    public required bool Terminal { get; init; }
    public string? SanitizedError { get; init; }
}
