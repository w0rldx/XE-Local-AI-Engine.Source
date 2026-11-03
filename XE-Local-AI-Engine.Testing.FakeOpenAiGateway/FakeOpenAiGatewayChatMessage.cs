namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>One message of a chat request, with its text parts joined.</summary>
public sealed record FakeOpenAiGatewayChatMessage
{
    public required string Role { get; init; }

    public required string Text { get; init; }
}
