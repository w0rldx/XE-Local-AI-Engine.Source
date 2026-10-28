namespace XE_Local_AI_Engine.Testing.FakeOllama;

using OllamaSharp.Models.Chat;

public sealed record FakeOllamaOptions
{
    public IReadOnlyList<string> Models { get; init; } = ["chat", "embeddings"];

    public Func<ChatRequest, IAsyncEnumerable<string>>? ChatTokenScript { get; init; }

    /// <summary>
    ///     Optional script run before the text-token path on every chat request, given the full
    ///     message list. Return a <see cref="FakeOllamaToolCall" /> to emit an Ollama
    ///     <c>tool_calls</c> chunk instead of text tokens, or <c>null</c> for the normal
    ///     <see cref="ChatTokenScript" /> / echo path, as on the call after a tool result.
    /// </summary>
    public Func<IReadOnlyList<Message>, FakeOllamaToolCall?>? ToolCallScript { get; init; }

    public int EmbeddingDimensions { get; init; } = 384;

    public string? ControlEndpointToken { get; init; }
}
