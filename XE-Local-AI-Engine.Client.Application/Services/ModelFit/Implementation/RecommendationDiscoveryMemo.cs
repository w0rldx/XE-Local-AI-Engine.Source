namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Shares Hugging Face discovery across the per-use-case recommendation runs that one "Refresh now" fires.
/// </summary>
/// <remarks>
///     The six per-use-case runs overlap in search terms and candidate repos. A search or inspection is shared while in
///     flight and reused for <see cref="ReuseWindow" />; a failed one is never reused. Each shared call has its own
///     timeout, so one caller giving up never cancels it for the others. Browse and download use the discovery directly.
/// </remarks>
internal sealed class RecommendationDiscoveryMemo : IHuggingFaceGgufDiscovery
{
    internal static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(2);

    // Matches the advisor's own per-call budget; the advisor still applies its own timeout to the wait.
    private static readonly TimeSpan SharedCallTimeout = TimeSpan.FromSeconds(20);

    // Prune by full scan once the map passes this size; entries are a few dozen per refresh.
    private const int PruneThreshold = 256;

    private readonly IHuggingFaceGgufDiscovery _inner;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<GgufSearchQuery, SharedCall<IReadOnlyList<GgufRepoSummary>>> _searches = new();
    private readonly ConcurrentDictionary<string, SharedCall<GgufRepoDetail>> _inspections = new(StringComparer.OrdinalIgnoreCase);

    public RecommendationDiscoveryMemo(IHuggingFaceGgufDiscovery inner, TimeProvider timeProvider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<IReadOnlyList<GgufRepoSummary>> SearchAsync(GgufSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Share(_searches, query, token => _inner.SearchAsync(query, token), ct);
    }

    public Task<GgufRepoDetail> InspectRepoAsync(string repoId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        return Share(_inspections, repoId, token => _inner.InspectRepoAsync(repoId, token), ct);
    }

    public Task<GgufRepoDetail> ListRepoFilesAsync(string repoId, CancellationToken ct)
    {
        return _inner.ListRepoFilesAsync(repoId, ct);
    }

    public Task<GgufProjectorFile?> FindProjectorAsync(string repoId, CancellationToken ct)
    {
        return _inner.FindProjectorAsync(repoId, ct);
    }

    private Task<T> Share<TKey, T>(ConcurrentDictionary<TKey, SharedCall<T>> calls,
        TKey key,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken ct)
        where TKey : notnull
    {
        var now = _timeProvider.GetUtcNow();
        if (calls.Count > PruneThreshold)
        {
            foreach (var stale in calls.Where(pair => !pair.Value.IsReusable(now)).ToList())
            {
                calls.TryRemove(stale);
            }
        }

        var call = calls.AddOrUpdate(key,
            _ => new SharedCall<T>(now, () => RunAsync(fetch)),
            (_, existing) => existing.IsReusable(now) ? existing : new SharedCall<T>(now, () => RunAsync(fetch)));
        return call.Task.WaitAsync(ct);
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> fetch)
    {
        using var timeout = new CancellationTokenSource(SharedCallTimeout, _timeProvider);
        return await fetch(timeout.Token);
    }

    private sealed class SharedCall<T>
    {
        private readonly Lazy<Task<T>> _task;

        public SharedCall(DateTimeOffset startedAt, Func<Task<T>> start)
        {
            StartedAt = startedAt;
            _task = new Lazy<Task<T>>(start, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private DateTimeOffset StartedAt { get; }

        public Task<T> Task => _task.Value;

        public bool IsReusable(DateTimeOffset now)
        {
            if (now - StartedAt >= ReuseWindow)
            {
                return false;
            }

            // A call not yet started (lost an AddOrUpdate race) or still running is shared; a failed one is retried.
            return !_task.IsValueCreated || !(_task.Value.IsFaulted || _task.Value.IsCanceled);
        }
    }
}
