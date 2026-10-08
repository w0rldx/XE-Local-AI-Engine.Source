namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>The reply for the next <see cref="Count" /> chat requests; a tool call wins over text.</summary>
public sealed record FakeOpenAiGatewayScript
{
    public string? CompletionText { get; init; }

    public FakeOpenAiGatewayToolCall? ToolCall { get; init; }

    public int Count { get; init; } = 1;
}
