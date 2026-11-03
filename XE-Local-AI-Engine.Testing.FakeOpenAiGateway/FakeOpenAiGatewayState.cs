namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

using System.Collections.Concurrent;

/// <summary>The fake gateway's live state: its options, the fault queue, the reply script and the request log.</summary>
public sealed class FakeOpenAiGatewayState
{
    private readonly ConcurrentQueue<FakeOpenAiGatewayFailure> _failures = new();
    private readonly ConcurrentQueue<FakeOpenAiGatewayRawResponse> _rawResponses = new();
    private readonly ConcurrentQueue<FakeOpenAiGatewayRequest> _requests = new();
    private readonly Lock _scriptLock = new();
    private FakeOpenAiGatewayScript? _script;
    private int _scriptRemaining;

    public FakeOpenAiGatewayState(FakeOpenAiGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    public FakeOpenAiGatewayOptions Options { get; }

    public IReadOnlyList<FakeOpenAiGatewayRequest> RecordedRequests => _requests.ToArray();

    public void EnqueueFailure(FakeOpenAiGatewayFailure failure)
    {
        _failures.Enqueue(failure);
    }

    /// <summary>Answer the next API request with exactly this status and JSON body, for a gateway's own error shape.</summary>
    public void EnqueueRawResponse(int status, string body)
    {
        _rawResponses.Enqueue(new FakeOpenAiGatewayRawResponse(status, body));
    }

    public bool TryDequeueRawResponse(out FakeOpenAiGatewayRawResponse response)
    {
        return _rawResponses.TryDequeue(out response);
    }

    public void ClearFailures()
    {
        _failures.Clear();
    }

    public bool TryDequeueFailure(out FakeOpenAiGatewayFailure failure)
    {
        return _failures.TryDequeue(out failure);
    }

    /// <summary>Replace the reply script; it answers the next <c>script.Count</c> chat requests.</summary>
    public void SetScript(FakeOpenAiGatewayScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        lock (_scriptLock)
        {
            _script = script;
            _scriptRemaining = script.Count;
        }
    }

    /// <summary>Take one use of the current script, or null when none is left.</summary>
    public FakeOpenAiGatewayScript? TakeScript()
    {
        lock (_scriptLock)
        {
            if (_script is null || _scriptRemaining <= 0)
            {
                return null;
            }

            _scriptRemaining--;
            return _script;
        }
    }

    public void Record(FakeOpenAiGatewayRequest request)
    {
        _requests.Enqueue(request);
    }

    public void ClearRequests()
    {
        _requests.Clear();
    }
}

/// <summary>A canned answer: exactly this status and JSON body.</summary>
public readonly record struct FakeOpenAiGatewayRawResponse(int Status, string Body);
