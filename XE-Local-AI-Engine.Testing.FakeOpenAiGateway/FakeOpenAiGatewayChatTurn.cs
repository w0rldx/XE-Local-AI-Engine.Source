namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>What a chat request asked for, as <see cref="FakeOpenAiGatewayState.ChatResponder" /> sees it.</summary>
public sealed record FakeOpenAiGatewayChatTurn
{
    public required string? Model { get; init; }

    public required IReadOnlyList<FakeOpenAiGatewayChatMessage> Messages { get; init; }

    /// <summary>The function names the request offered; a background request with no tools offers none.</summary>
    public required IReadOnlyList<string> OfferedToolNames { get; init; }
}
