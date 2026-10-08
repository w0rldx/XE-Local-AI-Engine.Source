namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>A tool call the fake gateway emits instead of text.</summary>
public sealed record FakeOpenAiGatewayToolCall
{
    public required string Name { get; init; }

    /// <summary>The arguments as the JSON string OpenAI puts in <c>function.arguments</c>.</summary>
    public string Arguments { get; init; } = "{}";
}
