namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IBenchmarkRunBatchService
{
    Task<BenchmarkRunBatchResult> StartAsync(BenchmarkRunBatchRequest request, CancellationToken cancellationToken = default);
}

public sealed record BenchmarkRunBatchRequest(Guid ProjectId,
    long ExpectedProjectVersion,
    IReadOnlyList<BenchmarkRunBatchItem> Items,
    int RepeatCount,
    bool Warmup,
    BenchmarkRepeatMode RepeatMode,
    double? AnswerVarianceTemperature);

public sealed record BenchmarkRunBatchItem(string ModelName, string? KvCacheType);

public sealed record BenchmarkRunBatchStartedItem(string ModelName, string? KvCacheType, IReadOnlyList<Guid> RunIds);

public enum BenchmarkRunBatchRejectionKind
{
    Failure,
    NotAttempted,
    TimeBudget
}

public sealed record BenchmarkRunBatchRejectedItem(string ModelName,
    string? KvCacheType,
    BenchmarkRunBatchRejectionKind Kind,
    string Message,
    Exception? Failure = null);

public sealed record BenchmarkRunBatchResult(long ProjectVersion,
    IReadOnlyList<BenchmarkRunBatchStartedItem> Started,
    IReadOnlyList<BenchmarkRunBatchRejectedItem> Rejected);

public sealed class BenchmarkRunBatchService(IBenchmarkRunFreezeService runs, TimeProvider timeProvider) : IBenchmarkRunBatchService
{
    private static readonly TimeSpan RequestTimeBudget = TimeSpan.FromSeconds(45);

    private readonly IBenchmarkRunFreezeService _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<BenchmarkRunBatchResult> StartAsync(BenchmarkRunBatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = new List<BenchmarkRunBatchStartedItem>(request.Items.Count);
        var rejected = new List<BenchmarkRunBatchRejectedItem>();
        await using var scope = new BenchmarkFreezeScope();
        var startedAt = _timeProvider.GetTimestamp();
        var expectedVersion = request.ExpectedProjectVersion;

        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index];
            if (_timeProvider.GetElapsedTime(startedAt) >= RequestTimeBudget)
            {
                var message = $"The batch reached its {RequestTimeBudget.TotalSeconds:0} second time budget after {started.Count} cell(s) started; "
                              + "resubmit the remaining items with the project version in this response.";
                AddRemainingRejections(request.Items, index, rejected, BenchmarkRunBatchRejectionKind.TimeBudget, message);
                break;
            }

            if (string.IsNullOrWhiteSpace(item.ModelName))
            {
                rejected.Add(Reject(item, new BenchmarkValidationException("A primary model is required.")));
                continue;
            }

            if (!BenchmarkKvCacheType.TryNormalize(item.KvCacheType, out var kvCacheType))
            {
                rejected.Add(Reject(item, new BenchmarkValidationException("The requested KV-cache type is not supported.")));
                continue;
            }

            try
            {
                var created = await _runs.StartAsync(new BenchmarkRunStartRequest(request.ProjectId,
                                                 item.ModelName,
                                                 expectedVersion,
                                                 kvCacheType,
                                                 request.RepeatCount,
                                                 request.Warmup,
                                                 request.RepeatMode,
                                                 request.AnswerVarianceTemperature), scope, cancellationToken)
                                             .ConfigureAwait(false);
                expectedVersion += created.Count;
                started.Add(new BenchmarkRunBatchStartedItem(item.ModelName,
                    kvCacheType,
                    [.. created.Select(static run => run.Id)]));
            }
            catch (Exception exception) when (IsWholeBatchFailure(exception))
            {
                if (started.Count == 0)
                {
                    throw;
                }

                rejected.Add(Reject(item, exception));
                var message = $"The batch stopped after {started.Count} cell(s) started; re-read the project version and resubmit the remaining items.";
                AddRemainingRejections(request.Items, index + 1, rejected, BenchmarkRunBatchRejectionKind.NotAttempted, message);
                break;
            }
            catch (Exception exception) when (IsPerItemFailure(exception))
            {
                rejected.Add(Reject(item, exception));
            }
        }

        return new BenchmarkRunBatchResult(expectedVersion, started, rejected);
    }

    private static bool IsWholeBatchFailure(Exception exception) =>
        exception is BenchmarkNotFoundException
        || (exception is BenchmarkConflictException conflict && string.Equals(conflict.Code, "VersionConflict", StringComparison.Ordinal));

    private static bool IsPerItemFailure(Exception exception) =>
        exception is BenchmarkStoreException or BenchmarkEligibilityException or BenchmarkUnsupportedKvCacheTypeException or KeyNotFoundException
            or NotSupportedException;

    private static BenchmarkRunBatchRejectedItem Reject(BenchmarkRunBatchItem item, Exception exception) =>
        new(item.ModelName, item.KvCacheType, BenchmarkRunBatchRejectionKind.Failure, exception.Message, exception);

    private static void AddRemainingRejections(IReadOnlyList<BenchmarkRunBatchItem> items,
        int startIndex,
        ICollection<BenchmarkRunBatchRejectedItem> rejected,
        BenchmarkRunBatchRejectionKind kind,
        string message)
    {
        for (var index = startIndex; index < items.Count; index++)
        {
            var item = items[index];
            rejected.Add(new BenchmarkRunBatchRejectedItem(item.ModelName, item.KvCacheType, kind, message));
        }
    }
}
