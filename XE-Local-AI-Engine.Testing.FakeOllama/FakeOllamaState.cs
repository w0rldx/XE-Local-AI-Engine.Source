namespace XE_Local_AI_Engine.Testing.FakeOllama;

using System.Collections.Concurrent;
using OllamaSharp.Models.Chat;

/// <summary>
///     Represents fake ollama state.
/// </summary>
public sealed class FakeOllamaState
{
    private readonly ConcurrentQueue<FakeOllamaFailure> _failures = new();
    private readonly ConcurrentQueue<FakeOllamaRequest> _requests = new();

    public FakeOllamaState(FakeOllamaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Models = options.Models.Count > 0 ? options.Models.ToArray() : ["chat", "embeddings"];
        ModelDigests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ModelInfo = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        RunningModels = [];
        ChatScript = options.ChatTokenScript;
        ToolCallScript = options.ToolCallScript;
        EmbeddingDimensions = options.EmbeddingDimensions > 0 ? options.EmbeddingDimensions : 384;
        ControlEndpointToken = options.ControlEndpointToken;
    }

    public IReadOnlyList<string> Models { get; set; }

    public IDictionary<string, string> ModelDigests { get; }

    public IDictionary<string, IReadOnlyDictionary<string, object?>> ModelInfo { get; }

    public IReadOnlyList<FakeOllamaRunningModel> RunningModels { get; set; }

    public Func<ChatRequest, IAsyncEnumerable<string>>? ChatScript { get; set; }

    /// <summary>
    ///     When set, checked before <see cref="ChatScript" /> on every chat request.
    ///     Return a <see cref="FakeOllamaToolCall" /> to emit an Ollama tool-call wire chunk;
    ///     return <c>null</c> to fall through to the normal text path.
    /// </summary>
    public Func<IReadOnlyList<Message>, FakeOllamaToolCall?>? ToolCallScript { get; set; }

    public int EmbeddingDimensions { get; set; }

    public string? ControlEndpointToken { get; }

    public IReadOnlyList<FakeOllamaRequest> RecordedRequests => _requests.ToArray();

    public void EnqueueFailure(FakeOllamaFailure failure)
    {
        _failures.Enqueue(failure);
    }

    public void ClearFailures()
    {
        Drain(_failures);
    }

    public bool TryDequeueFailure(out FakeOllamaFailure failure)
    {
        return _failures.TryDequeue(out failure);
    }

    public void Record(FakeOllamaRequest request)
    {
        _requests.Enqueue(request);
    }

    public void ClearRequests()
    {
        Drain(_requests);
    }

    private static void Drain<T>(ConcurrentQueue<T> queue)
    {
        while (queue.TryDequeue(out _))
        {
            Thread.Yield();
        }
    }

    /// <summary>
    ///     Value object carrying fake ollama running model data. <paramref name="SizeBytes" /> and
    ///     <paramref name="SizeVramBytes" /> mirror Ollama's <c>/api/ps</c> <c>size</c> / <c>size_vram</c> fields so the
    ///     loaded-models memory mapping is exercisable.
    /// </summary>
    public sealed record FakeOllamaRunningModel(
        string Name,
        DateTimeOffset? ExpiresAt,
        long SizeBytes = 0,
        long SizeVramBytes = 0);
}
