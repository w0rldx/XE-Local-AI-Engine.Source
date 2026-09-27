namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>
///     Stable SignalR client-method name for runtime acquisition status pushes. The React client subscribes to this
///     single method and reconciles each push against the hydrate snapshot by <see cref="RuntimeAcquisitionStatusHubMessage.Sequence" />.
/// </summary>
public static class RuntimeAcquisitionHubEvents
{
    /// <summary>The client method name a runtime acquisition status push is broadcast under.</summary>
    public const string StatusChanged = "runtimeAcquisition.statusChanged";
}

/// <summary>
///     Stable SignalR wire shape for a runtime acquisition status push, field for field the hydrate endpoint's
///     <c>RuntimeAcquisitionStatusResponse</c>. A 1:1 projection so a provider rename cannot reach the wire.
/// </summary>
internal sealed class RuntimeAcquisitionStatusHubMessage
{
    public required long Sequence { get; init; }
    public required string Phase { get; init; }
    public required string? Variant { get; init; }
    public required string? Tag { get; init; }
    public required long? CompletedBytes { get; init; }
    public required long? TotalBytes { get; init; }
    public required int StepIndex { get; init; }
    public required int StepCount { get; init; }
    public required string? SanitizedError { get; init; }
}
