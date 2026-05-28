namespace XE_Local_AI_Engine.Client.Services.Chat;

public static class LocalChatLoopbackDefaults
{
    public const string RequestedCapability = "local-chat-loopback";

    public const int EpochVersion = 1;
    public static Guid ClientNodeId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
