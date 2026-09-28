namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>
///     Simulates a smaller rerank pool: only the first <see cref="PoolSize" /> documents (the fusion head) are reranked;
///     the tail scores below the head's lowest, in order, so the stable descending sort keeps it in fusion order.
/// </summary>
/// <remarks>A pool no larger than <see cref="PoolSize" /> is a straight passthrough.</remarks>
internal sealed class TopNRerankPool : IRerankerClient
{
    private readonly IRerankerClient _inner;

    public TopNRerankPool(IRerankerClient inner, int poolSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(poolSize, 1);
        _inner = inner;
        PoolSize = poolSize;
    }

    public int PoolSize { get; }

    public async Task<IReadOnlyList<double>?> RerankAsync(string modelName, string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        if (documents.Count <= PoolSize)
        {
            return await _inner.RerankAsync(modelName, query, documents, cancellationToken);
        }

        var head = await _inner.RerankAsync(modelName, query, [.. documents.Take(PoolSize)], cancellationToken);
        if (head is null || head.Count != PoolSize)
        {
            return null;
        }

        var floor = head.Min();
        var scores = new double[documents.Count];
        for (var index = 0; index < documents.Count; index++)
        {
            scores[index] = index < PoolSize ? head[index] : floor - (index - PoolSize + 1);
        }

        return scores;
    }
}

/// <summary>
///     Negative control: returns the inner reranker's scores in a seeded random permutation, so every document keeps a
///     real-looking score that no longer belongs to it. The metrics must see this as worse than fusion alone.
/// </summary>
internal sealed class ShuffledScoresReranker : IRerankerClient
{
    private readonly IRerankerClient _inner;
    private readonly Random _random;

    public ShuffledScoresReranker(IRerankerClient inner, int seed)
    {
        _inner = inner;
        _random = new Random(seed);
    }

    public async Task<IReadOnlyList<double>?> RerankAsync(string modelName, string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        var scores = await _inner.RerankAsync(modelName, query, documents, cancellationToken);
        if (scores is null)
        {
            return null;
        }

        var shuffled = scores.ToArray();
        _random.Shuffle(shuffled);
        return shuffled;
    }
}

/// <summary>How one rerank call ended, as the search service saw it.</summary>
internal enum RerankOutcome
{
    /// <summary>Scores came back: the rerank applied.</summary>
    Scored,

    /// <summary>The client returned null: a logged degrade, the order stayed fusion.</summary>
    Degraded,

    /// <summary>The search's latency budget cancelled the call: a silent fallback the client never logs.</summary>
    BudgetCancelled
}

/// <summary>
///     Times the rerank stage and records each call's outcome. The service's budget token is the CALLER token to the
///     client, so a budget cancel is rethrown, never logged as a degrade: only this wrapper sees that fallback.
/// </summary>
/// <remarks>
///     The service stops WAITING at its deadline (<c>WaitAsync</c>) before the cancelled call unwinds, so the last call of
///     a run can still be in flight when the harness returns; <see cref="DrainAsync" /> waits for every call to record.
/// </remarks>
internal sealed class TimingReranker : IRerankerClient
{
    private readonly IRerankerClient _inner;
    private readonly ConcurrentQueue<(RerankOutcome Outcome, double Milliseconds, int DocumentCount, int Sequence)> _calls = new();
    private readonly ConcurrentQueue<Task> _inFlight = new();
    private int _started;

    public TimingReranker(IRerankerClient inner) =>
        _inner = inner;

    /// <summary>Outcomes in completion order; <c>Sequence</c> is the call's start order, 0-based.</summary>
    public IReadOnlyList<(RerankOutcome Outcome, double Milliseconds, int DocumentCount, int Sequence)> Calls => [.. _calls];

    /// <summary>How many calls have started so far, completed or not.</summary>
    public int StartedCalls => Volatile.Read(ref _started);

    /// <summary>Completes once every call started so far has recorded its outcome.</summary>
    public Task DrainAsync() =>
        Task.WhenAll(_inFlight);

    public Task<IReadOnlyList<double>?> RerankAsync(string modelName, string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        var call = TimeAsync(modelName, query, documents, Interlocked.Increment(ref _started) - 1, cancellationToken);
        _inFlight.Enqueue(call.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        return call;
    }

    private async Task<IReadOnlyList<double>?> TimeAsync(string modelName, string query, IReadOnlyList<string> documents, int sequence, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var scores = await _inner.RerankAsync(modelName, query, documents, cancellationToken);
            _calls.Enqueue((scores is null ? RerankOutcome.Degraded : RerankOutcome.Scored, Stopwatch.GetElapsedTime(started).TotalMilliseconds, documents.Count, sequence));
            return scores;
        }
        catch (OperationCanceledException)
        {
            _calls.Enqueue((RerankOutcome.BudgetCancelled, Stopwatch.GetElapsedTime(started).TotalMilliseconds, documents.Count, sequence));
            throw;
        }
    }
}

/// <summary>
///     Captures the reranker client's <c>LogDegrade</c> warnings (reason + document count) so a forced-rerank config that
///     silently fell back to fusion order is reported INVALID rather than as a quality number.
/// </summary>
internal sealed class DegradeCapturingLogger : ILogger<LlamaServerRerankerClient>
{
    private readonly ConcurrentQueue<(string Reason, int DocumentCount)> _degrades = new();

    public IReadOnlyList<(string Reason, int DocumentCount)> Degrades => [.. _degrades];

    public void Reset() =>
        _degrades.Clear();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) =>
        true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            return;
        }

        if (fields.FirstOrDefault(static field => field.Key == "Reason").Value is not string reason)
        {
            return;
        }

        var count = fields.FirstOrDefault(static field => field.Key == "DocumentCount").Value is int documentCount ? documentCount : -1;
        _degrades.Enqueue((reason, count));
    }
}

/// <summary>
///     Counts <see cref="KnowledgeSearchService" /> query-embedding degrades (the search fell back to lexical-only), so a
///     live config whose embedder failed at query time is reported INVALID instead of as a hybrid number.
/// </summary>
/// <remarks>
///     Matched by the product's stable <see cref="KnowledgeSearchService.QueryEmbeddingUnavailableEventId" />, so
///     rewording the message or renaming its fields cannot hide a degrade; a product test pins the EventId.
/// </remarks>
internal sealed class QueryEmbeddingDegradeLogger : ILogger<KnowledgeSearchService>
{
    private readonly ConcurrentQueue<string> _exceptionTypes = new();

    /// <summary>Exception type name of each degraded query since the last <see cref="Reset" />.</summary>
    public IReadOnlyList<string> ExceptionTypes => [.. _exceptionTypes];

    public void Reset() =>
        _exceptionTypes.Clear();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) =>
        true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (eventId.Id != KnowledgeSearchService.QueryEmbeddingUnavailableEventId)
        {
            return;
        }

        // The type name is diagnostic only; the count is what voids a row, so a missing field still counts.
        var exceptionType = state is IReadOnlyList<KeyValuePair<string, object?>> fields
            ? fields.FirstOrDefault(static field => field.Key == "ExceptionType").Value as string
            : null;
        _exceptionTypes.Enqueue(exceptionType ?? "unknown");
    }
}
