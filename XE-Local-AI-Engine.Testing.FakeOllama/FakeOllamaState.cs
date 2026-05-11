namespace XE_Local_AI_Engine.Testing.FakeOllama;

using System.Collections.Concurrent;
using OllamaSharp.Models.Chat;

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
        EmbeddingDimensions = options.EmbeddingDimensions > 0 ? options.EmbeddingDimensions : 384;
        ControlEndpointToken = options.ControlEndpointToken;
    }

    public IReadOnlyList<string> Models { get; set; }

    public IDictionary<string, string> ModelDigests { get; }

    public IDictionary<string, IReadOnlyDictionary<string, object?>> ModelInfo { get; }

    public IReadOnlyList<FakeOllamaRunningModel> RunningModels { get; set; }

    public Func<ChatRequest, IAsyncEnumerable<string>>? ChatScript { get; set; }

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

    public sealed record FakeOllamaRunningModel(string Name, DateTimeOffset? ExpiresAt);
}
