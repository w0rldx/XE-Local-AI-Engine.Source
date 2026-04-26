namespace XE_Local_AI_Engine.Testing.FakeOllama
{
    using OllamaSharp.Models.Chat;

    public sealed record FakeOllamaOptions
    {
        public IReadOnlyList<string> Models { get; init; } = ["chat", "embeddings"];

        public Func<ChatRequest, IAsyncEnumerable<string>>? ChatTokenScript { get; init; }

        public int EmbeddingDimensions { get; init; } = 384;

        public string? ControlEndpointToken { get; init; }
    }
}
