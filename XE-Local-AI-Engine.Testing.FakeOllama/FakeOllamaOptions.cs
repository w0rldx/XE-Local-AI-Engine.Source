namespace XE_Local_AI_Engine.Testing.FakeOllama;

using OllamaSharp.Models.Chat;

/// <summary>
///     Configuration options for fake ollama behavior.
/// </summary>
public sealed record FakeOllamaOptions
{
    public IReadOnlyList<string> Models { get; init; } = ["chat", "embeddings"];

    public Func<ChatRequest, IAsyncEnumerable<string>>? ChatTokenScript { get; init; }

    /// <summary>
    ///     Optional script invoked before the text-token path on every chat request.
    ///     Receives the full message list; return a <see cref="FakeOllamaToolCall" /> to emit an
    ///     Ollama <c>tool_calls</c> wire chunk instead of text tokens, or <c>null</c> to fall
    ///     through to the normal <see cref="ChatTokenScript" /> / echo path.
    ///     On the second call (after the engine has appended a tool-result message) return
    ///     <c>null</c> so the final text reply streams normally.
    /// </summary>
    public Func<IReadOnlyList<Message>, FakeOllamaToolCall?>? ToolCallScript { get; init; }

    public int EmbeddingDimensions { get; init; } = 384;

    public string? ControlEndpointToken { get; init; }
}
