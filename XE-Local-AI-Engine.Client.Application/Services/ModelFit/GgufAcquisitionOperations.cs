namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using System.Collections.Concurrent;

public enum GgufAcquisitionOperationKind
{
    Download,
    Import
}

public enum GgufAcquisitionPhase
{
    // Kept for wire/backward compatibility with callers that only understand a generic active phase.
    Running = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
    Validating = 4,
    Downloading = 5,
    Copying = 6,
    Committing = 7
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
    GgufAcquisitionStatus RecordTerminal(GgufAcquisitionOperationKind operationKind,
        string modelName,
        GgufAcquisitionPhase phase,
        long? completedBytes = null,
        long? totalBytes = null,
        string? errorCode = null,
        string? sanitizedError = null);
    GgufAcquisitionStatus Update(Guid operationId,
        GgufAcquisitionPhase phase,
        long? completedBytes = null,
        long? totalBytes = null,
        string? errorCode = null,
        string? sanitizedError = null);
}

public sealed class GgufAcquisitionOperationRegistry : IGgufAcquisitionOperationRegistry
{
    public const int DefaultMaxTerminalCount = 256;
    public static readonly TimeSpan DefaultTerminalMaxAge = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<(GgufAcquisitionOperationKind Kind, string ModelKey), Guid> _active = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, GgufAcquisitionStatus> _statuses = new();
    private readonly int _maxTerminalCount;
    private readonly Lock _pruneGate = new();
    private readonly TimeSpan _terminalMaxAge;
    private readonly TimeProvider _timeProvider;
    private long _lastTimestampTicks;

    public GgufAcquisitionOperationRegistry(TimeProvider timeProvider,
        TimeSpan? terminalMaxAge = null,
        int maxTerminalCount = DefaultMaxTerminalCount)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _terminalMaxAge = terminalMaxAge ?? DefaultTerminalMaxAge;
        if (_terminalMaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalMaxAge), "Terminal retention age must be positive.");
        }

        if (maxTerminalCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTerminalCount), "Terminal retention count must be positive.");
        }

        _maxTerminalCount = maxTerminalCount;
    }

    public GgufAcquisitionRegistration Start(GgufAcquisitionOperationKind operationKind, string modelName, long? totalBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        PruneTerminals();
        var normalizedName = modelName.Trim();
        var activeKey = (operationKind, normalizedName.ToUpperInvariant());
        while (true)
        {
            if (_active.TryGetValue(activeKey, out var existingId)
                && _statuses.TryGetValue(existingId, out var existing)
                && IsActive(existing.Phase)
                && _cancellations.TryGetValue(existingId, out var existingCancellation))
            {
                return new GgufAcquisitionRegistration(existing, AlreadyInFlight: true, existingCancellation.Token);
            }

            var operationId = Guid.NewGuid();
            var cancellation = new CancellationTokenSource();
            var now = NextTimestamp();
            var status = new GgufAcquisitionStatus(operationId,
                operationKind,
                normalizedName,
                GgufAcquisitionPhase.Validating,
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

    public GgufAcquisitionStatus? GetStatus(Guid operationId)
    {
        PruneTerminals();
        return _statuses.GetValueOrDefault(operationId);
    }

    public GgufAcquisitionStatus? GetNewest(GgufAcquisitionOperationKind operationKind, string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        PruneTerminals();
        return _statuses.Values
                        .Where(status => status.OperationKind == operationKind
                                         && string.Equals(status.ModelName, modelName.Trim(), StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(static status => status.StartedAtUtc)
                        .ThenByDescending(static status => status.OperationId)
                        .FirstOrDefault();
    }

    public IReadOnlyList<GgufAcquisitionStatus> List(GgufAcquisitionOperationKind operationKind)
    {
        PruneTerminals();
        return _statuses.Values.Where(status => status.OperationKind == operationKind)
                        .OrderByDescending(static status => status.StartedAtUtc)
                        .ThenByDescending(static status => status.OperationId)
                        .ToArray();
    }

    public GgufAcquisitionStatus RecordTerminal(GgufAcquisitionOperationKind operationKind,
        string modelName,
        GgufAcquisitionPhase phase,
        long? completedBytes = null,
        long? totalBytes = null,
        string? errorCode = null,
        string? sanitizedError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (IsActive(phase))
        {
            throw new ArgumentException("A directly-recorded acquisition status must be terminal.", nameof(phase));
        }

        var now = NextTimestamp();
        var status = new GgufAcquisitionStatus(Guid.NewGuid(),
            operationKind,
            modelName.Trim(),
            phase,
            completedBytes,
            totalBytes,
            now,
            now,
            errorCode,
            sanitizedError);
        _statuses[status.OperationId] = status;
        PruneTerminals();
        return status;
    }

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
                if (!IsActive(current.Phase))
                {
                    updated = current;
                    return current;
                }

                updated = current with
                {
                    Phase = phase,
                    CompletedBytes = completedBytes ?? current.CompletedBytes,
                    TotalBytes = totalBytes ?? current.TotalBytes,
                    UpdatedAtUtc = NextTimestamp(current.UpdatedAtUtc),
                    ErrorCode = errorCode,
                    SanitizedError = sanitizedError
                };
                return updated;
            });
        var result = updated ?? throw new InvalidOperationException("The acquisition status update did not produce a result.");
        if (!IsActive(phase))
        {
            var key = (result.OperationKind, result.ModelName.ToUpperInvariant());
            _active.TryRemove(KeyValuePair.Create(key, operationId));
            if (_cancellations.TryRemove(operationId, out var cancellation))
            {
                cancellation.Dispose();
            }

            PruneTerminals();
        }

        return result;
    }

    private static bool IsActive(GgufAcquisitionPhase phase) => phase is GgufAcquisitionPhase.Validating
        or GgufAcquisitionPhase.Downloading
        or GgufAcquisitionPhase.Copying
        or GgufAcquisitionPhase.Committing
        or GgufAcquisitionPhase.Running;

    private DateTimeOffset NextTimestamp(DateTimeOffset? after = null)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _lastTimestampTicks);
            var candidate = Math.Max(_timeProvider.GetUtcNow().UtcTicks, observed + 1);
            if (after is not null)
            {
                candidate = Math.Max(candidate, after.Value.UtcTicks + 1);
            }

            if (Interlocked.CompareExchange(ref _lastTimestampTicks, candidate, observed) == observed)
            {
                return new DateTimeOffset(candidate, TimeSpan.Zero);
            }
        }
    }

    private void PruneTerminals()
    {
        lock (_pruneGate)
        {
            var cutoff = _timeProvider.GetUtcNow() - _terminalMaxAge;
            var terminals = _statuses.Values.Where(static status => !IsActive(status.Phase))
                                     .OrderBy(static status => status.UpdatedAtUtc)
                                     .ThenBy(static status => status.OperationId)
                                     .ToArray();
            foreach (var expired in terminals.Where(status => status.UpdatedAtUtc < cutoff))
            {
                _statuses.TryRemove(expired.OperationId, out _);
            }

            var retained = terminals.Where(status => status.UpdatedAtUtc >= cutoff).ToArray();
            var excess = retained.Length - _maxTerminalCount;
            for (var index = 0; index < excess; index++)
            {
                _statuses.TryRemove(retained[index].OperationId, out _);
            }
        }
    }

}
