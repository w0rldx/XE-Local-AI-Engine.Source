namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The deterministic identity a node uses for a turn it dispatched to itself. Read by
///     <c>AgentHomeIdentityProvider</c> when no stored worker credentials name a node id, and by every runtime-package
///     builder as the package's client node id.
/// </summary>
public static class LocalChatLoopbackDefaults
{
    public static Guid ClientNodeId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
