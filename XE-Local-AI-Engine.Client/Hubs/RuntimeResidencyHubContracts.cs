namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>Stable SignalR client-method name for the runtime residency change tick.</summary>
public static class RuntimeResidencyHubEvents
{
    /// <summary>The client method name a residency change tick is broadcast under.</summary>
    public const string Changed = "runtimeResidency.changed";
}

/// <summary>Stable SignalR wire shape for a residency change tick: a monotonic sequence and nothing else.</summary>
internal sealed class RuntimeResidencyChangedHubMessage
{
    public required long Sequence { get; init; }
}
