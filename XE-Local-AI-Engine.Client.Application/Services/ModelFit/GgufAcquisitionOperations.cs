namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using System.Collections.Concurrent;

public enum GgufAcquisitionOperationKind
{
    Download,
    Import
}

public enum GgufAcquisitionPhase
{
    Running,
    Completed,
    Cancelled,
    Failed
}

public sealed record GgufAcquisitionStatus(
    Guid OperationId,
    GgufAcquisitionOperationKind OperationKind,
    string ModelName,
    GgufAcquisitionPhase Phase,
    long? CompletedBytes,
    long? TotalBytes,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ErrorCode,
    string? SanitizedError);

public sealed record GgufAcquisitionRegistration(
    GgufAcquisitionStatus Status,
    bool AlreadyInFlight,
    CancellationToken CancellationToken);

public interface IGgufAcquisitionOperationRegistry
{
    GgufAcquisitionRegistration Start(GgufAcquisitionOperationKind operationKind, string modelName, long? totalBytes = null);
    bool Cancel(Guid operationId);
    bool CancelNewest(GgufAcquisitionOperationKind operationKind, string modelName);
    GgufAcquisitionStatus? GetStatus(Guid operationId);
    GgufAcquisitionStatus? GetNewest(GgufAcquisitionOperationKind operationKind, string modelName);
    IReadOnlyList<GgufAcquisitionStatus> List(GgufAcquisitionOperationKind operationKind);
    GgufAcquisitionStatus Update(Guid operationId,
        GgufAcquisitionPhase phase,
        long? completedBytes = null,
        long? totalBytes = null,
        string? errorCode = null,
        string? sanitizedError = null);
}

public sealed class GgufAcquisitionOperationRegistry(TimeProvider timeProvider) : IGgufAcquisitionOperationRegistry
{
    private readonly ConcurrentDictionary<(GgufAcquisitionOperationKind Kind, string ModelKey), Guid> _active = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, GgufAcquisitionStatus> _statuses = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public GgufAcquisitionRegistration Start(GgufAcquisitionOperationKind operationKind, string modelName, long? totalBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var normalizedName = modelName.Trim();
        var activeKey = (operationKind, normalizedName.ToUpperInvariant());
        while (true)
        {
            if (_active.TryGetValue(activeKey, out var existingId)
                && _statuses.TryGetValue(existingId, out var existing)
                && existing.Phase == GgufAcquisitionPhase.Running
                && _cancellations.TryGetValue(existingId, out var existingCancellation))
            {
                return new GgufAcquisitionRegistration(existing, AlreadyInFlight: true, existingCancellation.Token);
            }

            var operationId = Guid.NewGuid();
            var cancellation = new CancellationTokenSource();
            var now = _timeProvider.GetUtcNow();
            var status = new GgufAcquisitionStatus(operationId,
                operationKind,
                normalizedName,
                GgufAcquisitionPhase.Running,
                CompletedBytes: null,
                totalBytes,
                now,
                now,
                ErrorCode: null,
                SanitizedError: null);
            _statuses[operationId] = status;
            _cancellations[operationId] = cancellation;
            if (_active.TryAdd(activeKey, operationId))
            {
                return new GgufAcquisitionRegistration(status, AlreadyInFlight: false, cancellation.Token);
            }

            _statuses.TryRemove(operationId, out _);
            if (_cancellations.TryRemove(operationId, out var rejectedCancellation))
            {
                rejectedCancellation.Dispose();
            }
        }
    }

    public bool Cancel(Guid operationId)
    {
        if (!_cancellations.TryGetValue(operationId, out var cancellation))
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool CancelNewest(GgufAcquisitionOperationKind operationKind, string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var key = (operationKind, modelName.Trim().ToUpperInvariant());
        return _active.TryGetValue(key, out var operationId) && Cancel(operationId);
    }

    public GgufAcquisitionStatus? GetStatus(Guid operationId) => _statuses.GetValueOrDefault(operationId);

    public GgufAcquisitionStatus? GetNewest(GgufAcquisitionOperationKind operationKind, string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _statuses.Values
                        .Where(status => status.OperationKind == operationKind
                                         && string.Equals(status.ModelName, modelName.Trim(), StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(static status => status.StartedAtUtc)
                        .ThenByDescending(static status => status.OperationId)
                        .FirstOrDefault();
    }

    public IReadOnlyList<GgufAcquisitionStatus> List(GgufAcquisitionOperationKind operationKind) =>
        _statuses.Values.Where(status => status.OperationKind == operationKind)
                 .OrderByDescending(static status => status.StartedAtUtc)
                 .ThenByDescending(static status => status.OperationId)
                 .ToArray();

    public GgufAcquisitionStatus Update(Guid operationId,
        GgufAcquisitionPhase phase,
        long? completedBytes = null,
        long? totalBytes = null,
        string? errorCode = null,
        string? sanitizedError = null)
    {
        GgufAcquisitionStatus? updated = null;
        _statuses.AddOrUpdate(operationId,
            static id => throw new KeyNotFoundException($"The acquisition operation '{id}' was not found."),
            (_, current) =>
            {
                if (current.Phase != GgufAcquisitionPhase.Running)
                {
                    updated = current;
                    return current;
                }

                updated = current with
                {
                    Phase = phase,
                    CompletedBytes = completedBytes ?? current.CompletedBytes,
                    TotalBytes = totalBytes ?? current.TotalBytes,
                    UpdatedAtUtc = _timeProvider.GetUtcNow(),
                    ErrorCode = errorCode,
                    SanitizedError = sanitizedError
                };
                return updated;
            });
        var result = updated ?? throw new InvalidOperationException("The acquisition status update did not produce a result.");
        if (phase != GgufAcquisitionPhase.Running)
        {
            var key = (result.OperationKind, result.ModelName.ToUpperInvariant());
            _active.TryRemove(KeyValuePair.Create(key, operationId));
            if (_cancellations.TryRemove(operationId, out var cancellation))
            {
                cancellation.Dispose();
            }
        }

        return result;
    }

}
