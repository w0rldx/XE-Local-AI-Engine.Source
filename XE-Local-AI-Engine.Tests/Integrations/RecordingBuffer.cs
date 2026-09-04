namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     An <see cref="IIntegrationExecutionEventBuffer" /> that records what was RESERVED, PUBLISHED and ABANDONED, so a
///     suite can assert the durable-before-visible ordering and the "exactly one of publish or abandon per reservation"
///     invariant directly rather than inferring them from the rows that survived.
/// </summary>
internal sealed class RecordingBuffer(long initialSequence = 0) : IIntegrationExecutionEventBuffer
{
    private readonly Lock _gate = new();

    /// <summary>
    ///     A LATCH, not a one-shot signal: it stays completed once the first reservation is taken, so a suite that asks
    ///     for it after the reservation already happened is not left waiting on a source nothing will ever set.
    /// </summary>
    private readonly TaskCompletionSource _reserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _sequence = initialSequence;

    public List<long> Reserved { get; } = [];

    public List<IntegrationStreamEvent> Published { get; } = [];

    public List<long> Abandoned { get; } = [];

    /// <summary>
    ///     Ids this double answers as UNTRACKED, which is what a post-terminal <c>Remove</c> leaves behind. The real
    ///     buffer throws on <c>Reserve</c> for one; nothing else about this double changes, so a suite that does not opt
    ///     in sees the old behaviour.
    /// </summary>
    public HashSet<Guid> Untracked { get; } = [];

    /// <summary>Completes once a reservation has been taken, so a blocking-commit test never sleeps on a guess.</summary>
    public Task WaitForReserveAsync() => _reserved.Task;

    public long Reserve(Guid executionId)
    {
        lock (_gate)
        {
            if (Untracked.Contains(executionId))
            {
                throw new InvalidOperationException($"Integration execution {executionId} has no event buffer entry. Call TryCreate before minting a sequence.");
            }

            _sequence++;
            Reserved.Add(_sequence);
            _ = _reserved.TrySetResult();
            return _sequence;
        }
    }

    public void Publish(IntegrationStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        lock (_gate)
        {
            Published.Add(streamEvent);
        }
    }

    public void Abandon(Guid executionId, long sequence)
    {
        lock (_gate)
        {
            Abandoned.Add(sequence);
        }
    }

    public bool TryCreate(Guid executionId, long initialSequence = 0) => true;

    public void Remove(Guid executionId)
    {
    }

    public IntegrationStreamEvent Append(Guid executionId, Guid sessionId, string type, string? contentType, JsonElement? payload)
    {
        lock (_gate)
        {
            _sequence++;
            var appended = new IntegrationStreamEvent(type, _sequence, executionId, sessionId, OccurredAtUtc: 0, contentType, payload);
            Published.Add(appended);
            return appended;
        }
    }

    public long LowestPendingReservation(Guid executionId) => long.MaxValue;

    public bool IsTracked(Guid executionId) => !Untracked.Contains(executionId);

    public long LastSequence(Guid executionId) => _sequence;

    public long Floor(Guid executionId) => 1;

    public IAsyncEnumerable<IntegrationStreamEvent> ReadAsync(Guid executionId, long sinceSequence, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This double records writes; the reader is the real buffer's own suite.");
}
