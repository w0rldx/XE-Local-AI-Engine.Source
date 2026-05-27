namespace XE_Local_AI_Engine.Testing.FakeOllama;

/// <summary>
///     Describes a single tool call the fake Ollama server should emit in an assistant turn.
///     Set <see cref="FakeOllamaOptions.ToolCallScript" /> to return one of these when you
///     want FakeOllama to produce a deterministic tool-call response instead of a text reply.
/// </summary>
public sealed record FakeOllamaToolCall
{
    /// <summary>The function name to request. Must match a registered AIFunction name exactly.</summary>
    public required string Name { get; init; }

    /// <summary>
    ///     JSON-serialisable arguments object. Will be serialised as the <c>arguments</c> field
    ///     in the Ollama <c>tool_calls[0].function</c> wire chunk.
    ///     Example: <c>new { expression = "12*9" }</c>
    /// </summary>
    public required object Arguments { get; init; }
}
