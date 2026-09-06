namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for integration executions and their event feed.
///     <para>
///         Every method here goes through EF so both encryption interceptors run — the terminal event's
///         <c>detail_json</c> is an encrypted column, and only a <c>SaveChanges</c> seals it. The single exception is
///         <see cref="AcceptAsync" /> in the sibling partial file, which opens its own connection and takes SQLite's
///         write lock with <c>BEGIN IMMEDIATE</c> because it is the only method that needs a hard bound rather than a
///         racy read.
///     </para>
/// </summary>
public sealed partial class IntegrationExecutionStore : IIntegrationExecutionStore
{
    /// <summary>
    ///     The brief's bound on a non-output event's detail, in UTF-8 bytes. <c>external.output</c> is exempt because it
    ///     carries the caller-facing payload and is bounded at <c>IntegrationOptions.MaxOutputBytes</c> by the append
    ///     path that writes it.
    /// </summary>
    private const int MaxEventDetailBytes = 4096;

    /// <summary>The one exempt event type. A literal, because this assembly cannot see the application's type list.</summary>
    private const string ExternalOutputEventType = "external.output";

    private readonly string _connectionString;
    private readonly NodeChatDbContext _dbContext;

    public IntegrationExecutionStore(NodeChatDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _connectionString = dbContext.Database.GetConnectionString()
                            ?? throw new InvalidOperationException("The integration execution store requires a configured SQLite connection string.");
    }

    public async Task<IntegrationExecutionSnapshot?> GetByIdAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.IntegrationExecutions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == executionId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IntegrationExecutionSnapshot?> GetByRequestIdAsync(Guid principalId, Guid requestId, CancellationToken cancellationToken = default)
    {
        // Both columns: the unique index over (principal_id, request_id) guarantees this matches at most one row, and
        // scoping by principal is what stops a replay ever seeing another integrator's execution.
        var entity = await _dbContext.IntegrationExecutions.AsNoTracking()
                                     .SingleOrDefaultAsync(row => row.PrincipalId == principalId && row.RequestId == requestId, cancellationToken)
                                     .ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IReadOnlyList<IntegrationExecutionSnapshot>> ListAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // The Id tie-break is load-bearing: ReceivedAtUtc is a millisecond stamp, so two accepts in the same
        // millisecond would page non-deterministically and drop or duplicate a row across pages.
        var entities = await Matching(filter)
                             .OrderByDescending(row => row.ReceivedAtUtc)
                             .ThenByDescending(row => row.Id)
                             .Skip(Math.Max(val1: 0, filter.Offset))
                             .Take(Math.Max(val1: 0, filter.Limit))
                             .ToListAsync(cancellationToken)
                             .ConfigureAwait(false);
        return [.. entities.Select(ToSnapshot)];
    }

    public Task<int> CountAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // No Skip/Take: the total a pager labels its window with is the whole matching set, not the window.
        return Matching(filter).CountAsync(cancellationToken);
    }

    /// <summary>
    ///     The filter half of both reads, in ONE place: a count computed from a second copy of these predicates would
    ///     drift from the page it labels the moment either side gained a filter.
    /// </summary>
    private IQueryable<IntegrationExecution> Matching(IntegrationExecutionFilter filter)
    {
        var query = _dbContext.IntegrationExecutions.AsNoTracking();

        if (filter.TriggerId is { } triggerId)
        {
            query = query.Where(row => row.TriggerId == triggerId);
        }

        if (filter.SessionId is { } sessionId)
        {
            query = query.Where(row => row.SessionId == sessionId);
        }

        if (filter.Status is { Count: > 0 } statuses)
        {
            // Materialized to an array because EF translates Enumerable.Contains over one into an IN list; the
            // IReadOnlySet<T>.Contains the set itself offers is an interface call it cannot see through.
            var wanted = statuses.ToArray();
            query = query.Where(row => wanted.Contains(row.Status));
        }

        return query;
    }

    public Task<int> CountActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _dbContext.IntegrationExecutions.AsNoTracking()
                  .CountAsync(row => row.SessionId == sessionId
                                     && (row.Status == IntegrationExecutionStatus.Accepted
                                         || row.Status == IntegrationExecutionStatus.Queued
                                         || row.Status == IntegrationExecutionStatus.Running),
                      cancellationToken);

    public async Task<bool> UpdateStatusAsync(IntegrationExecutionStatusUpdate command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = await _dbContext.IntegrationExecutions.SingleOrDefaultAsync(row => row.Id == command.ExecutionId, cancellationToken).ConfigureAwait(false);
        if (entity is null || entity.Version != command.ExpectedVersion || !command.ExpectedStatuses.Contains(entity.Status))
        {
            return false;
        }

        entity.Status = command.NewStatus;

        // A null optional field means "leave it alone", never "clear it" — the opposite of TryTerminalizeAsync's rule.
        if (command.StartedAtUtc is { } startedAtUtc)
        {
            entity.StartedAtUtc = startedAtUtc;
        }

        if (command.EndedAtUtc is { } endedAtUtc)
        {
            entity.EndedAtUtc = endedAtUtc;
        }

        if (command.InvocationId is { } invocationId)
        {
            entity.InvocationId = invocationId;
        }

        if (command.StopRequestedAtUtc is { } stopRequestedAtUtc)
        {
            entity.StopRequestedAtUtc = stopRequestedAtUtc;
        }

        if (command.FailureCategory is { } failureCategory)
        {
            entity.FailureCategory = failureCategory;
        }

        if (command.FailureSummary is { } failureSummary)
        {
            entity.FailureSummary = failureSummary;
        }

        entity.Version++;

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The query-first check alone is not atomic; the version's concurrency-token mapping is what makes two
            // concurrent compare-and-swaps resolve to exactly one winner, and the loser must learn it lost without a
            // try/catch of its own.
            _dbContext.ChangeTracker.Clear();
            return false;
        }
        catch (DbUpdateException)
        {
            // Any other failed save leaves the mutated entity tracked; clear it so the next call on a scoped context
            // does not replay this one's changes.
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        return true;
    }

    public async Task<bool> TryTerminalizeAsync(IntegrationTerminalizeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = await _dbContext.IntegrationExecutions.SingleOrDefaultAsync(row => row.Id == command.ExecutionId, cancellationToken).ConfigureAwait(false);
        if (entity is null || entity.Version != command.ExpectedVersion || !command.ExpectedStatuses.Contains(entity.Status))
        {
            return false;
        }

        entity.Status = command.NewStatus;
        entity.EndedAtUtc = command.EndedAtUtc;

        // ASSIGNED, not merged: a terminal write is the final word on why a run ended, so a Completed command carrying
        // two nulls must clear a category an earlier attempt left on the row.
        entity.FailureCategory = command.FailureCategory;
        entity.FailureSummary = command.FailureSummary;
        entity.Version++;

        _ = _dbContext.IntegrationExecutionEvents.Add(new IntegrationExecutionEvent
        {
            Id = Guid.NewGuid(),
            ExecutionId = command.ExecutionId,
            Sequence = command.Sequence,
            EventType = command.EventType,
            // The caller's own payload when it built one; the failure columns are the fallback for a caller that did
            // not, so a terminal written straight through this store still carries its reason.
            DetailJson = command.EventDetailJson is { } detail ? Encoding.UTF8.GetBytes(detail) : FailureDetail(command.FailureCategory, command.FailureSummary),
            OccurredAtUtc = command.EndedAtUtc
        });

        // The kind-3 audit row rides the SAME SaveChanges as the status and the event. Written only by the winner of
        // the CAS, because a lost CAS rolls the whole transaction back and never reaches the commit below.
        if (command.Audit is { } audit)
        {
            _ = _dbContext.AgentExecutionLogs.Add(AgentExecutionLogStore.BuildIntegrationInvocation(audit, command.EndedAtUtc));
        }

        // ONE transaction around the status CAS, the terminal event, the audit row and both watermarks: the watermarks
        // move through SQL rather than through a loaded value, so two writers racing on the same row cannot each apply
        // their own stale MAX and lose the higher one.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await MoveWatermarksAsync(command.ExecutionId, entity.SessionId, command.Sequence, command.EndedAtUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            return false;
        }
        catch (Exception)
        {
            // EVERYTHING inside the transaction, not just the save: a watermark update or the commit that throws rolls
            // the database back while EF still holds the saved terminal entity as committed, and the next call on this
            // scoped context — the fault handler's own terminalization — then compare-and-swaps against that stale
            // identity-map version and loses forever, stranding the row non-terminal with its admission slot held.
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        return true;
    }

    public async Task AppendEventAsync(IntegrationEventAppend command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.DetailJson is { } detail
            && !string.Equals(command.EventType, ExternalOutputEventType, StringComparison.Ordinal)
            && Encoding.UTF8.GetByteCount(detail) > MaxEventDetailBytes)
        {
            throw new ArgumentException($"A '{command.EventType}' event's detail may not exceed {MaxEventDetailBytes} UTF-8 bytes.", nameof(command));
        }

        // AsNoTracking: nothing on the row is mutated here any more — both watermarks move through SQL below — and a
        // tracked copy would only carry a stale LastSequence into the next call on this scoped context.
        var entity = await _dbContext.IntegrationExecutions.AsNoTracking()
                                     .SingleOrDefaultAsync(row => row.Id == command.ExecutionId, cancellationToken)
                                     .ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Integration execution '{command.ExecutionId}' does not exist.");

        _ = _dbContext.IntegrationExecutionEvents.Add(new IntegrationExecutionEvent
        {
            Id = command.EventId,
            ExecutionId = command.ExecutionId,
            Sequence = command.Sequence,
            EventType = command.EventType,
            DetailJson = command.DetailJson is null ? null : Encoding.UTF8.GetBytes(command.DetailJson),
            OccurredAtUtc = command.OccurredAtUtc
        });

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await MoveWatermarksAsync(command.ExecutionId, entity.SessionId, command.Sequence, command.OccurredAtUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A duplicate execution-and-sequence pair is a caller bug and rethrows, but the failed event stays
            // tracked; without this clear, the next append on the same scoped context replays it. The boundary covers
            // the watermarks and the commit too, for the same reason it does in TryTerminalizeAsync.
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<IntegrationExecutionSnapshot?> FindActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // A session runs at most one execution at a time — the accept path's per-session gate is what guarantees it —
        // so FirstOrDefault is a statement about that invariant rather than a tolerance for two.
        var entity = await _dbContext.IntegrationExecutions.AsNoTracking()
                                     .Where(row => row.SessionId == sessionId && row.Status == IntegrationExecutionStatus.Running)
                                     .OrderByDescending(row => row.ReceivedAtUtc)
                                     .FirstOrDefaultAsync(cancellationToken)
                                     .ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<bool> AppendOutputEventAsync(IntegrationEventAppend append, long maxOutputBytesPerExecution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(append);

        if (append.DetailJson is null)
        {
            throw new ArgumentException("An external.output event must carry its composed payload.", nameof(append));
        }

        // The store measures, so the number CHECKED and the number ADDED cannot disagree with each other or with the
        // caller's own pre-check. Plaintext UTF-8, never the encrypted column's length.
        var length = (long)Encoding.UTF8.GetByteCount(append.DetailJson);

        var entity = await _dbContext.IntegrationExecutions.SingleOrDefaultAsync(row => row.Id == append.ExecutionId, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Integration execution '{append.ExecutionId}' does not exist.");

        // Check-and-reserve INSIDE the transaction that inserts the row. This is the authoritative cap; over it,
        // nothing at all is written — not the event, not either counter.
        if (entity.OutputBytes + length > maxOutputBytesPerExecution)
        {
            return false;
        }

        _ = _dbContext.IntegrationExecutionEvents.Add(new IntegrationExecutionEvent
        {
            Id = append.EventId,
            ExecutionId = append.ExecutionId,
            Sequence = append.Sequence,
            EventType = append.EventType,
            DetailJson = Encoding.UTF8.GetBytes(append.DetailJson),
            OccurredAtUtc = append.OccurredAtUtc
        });

        entity.OutputBytes += length;
        entity.OutputCount++;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await MoveWatermarksAsync(append.ExecutionId, entity.SessionId, append.Sequence, append.OccurredAtUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The mutated entity and the pending event stay tracked otherwise, and the next call on this scoped context
            // would replay them on its own save. Widened past the save for the same reason as the terminal path: a
            // rolled-back watermark update must not leave this row's counters tracked as committed.
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        return true;
    }

    public async Task<IReadOnlyList<IntegrationExecutionEventSnapshot>> ListEventsAsync(Guid executionId,
        long sinceSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "An event page limit must be positive.");
        }

        // Materialize the entities FIRST and map afterwards. Projecting the columns inside the LINQ query materializes
        // no entity, so the decryption interceptor never runs and the caller gets ciphertext.
        var events = await _dbContext.IntegrationExecutionEvents.AsNoTracking()
                                     .Where(row => row.ExecutionId == executionId && row.Sequence > sinceSequence)
                                     .OrderBy(row => row.Sequence)
                                     .Take(limit)
                                     .ToListAsync(cancellationToken)
                                     .ConfigureAwait(false);

        return
        [
            .. events.Select(static row => new IntegrationExecutionEventSnapshot(row.Id,
                row.ExecutionId,
                row.Sequence,
                row.EventType,
                TextOrNull(row.DetailJson),
                row.OccurredAtUtc))
        ];
    }

    /// <summary>
    ///     Both watermarks, in SQL, inside the caller's open transaction — never through a value loaded before the
    ///     event was written. Two writers on one execution (the stream mapper's pump and <c>emit_output</c>) each read
    ///     their own <c>LastSequence</c>, applied <c>Math.Max</c> in memory and saved through separate contexts, so the
    ///     slower one overwrote the higher watermark with its own stale number. Recovery then seeded the replay ring
    ///     below a sequence that already had a row.
    ///     <para>
    ///         The execution's watermark is a running MAXIMUM computed by the database. The session's stays a plain
    ///         assignment: sequences restart at 1 per execution, so a maximum across a session's executions would
    ///         freeze at the deepest old stream and never move again — it is the activity indicator the UI renders,
    ///         not an ordering key, and last-writer-wins is the behaviour it wants.
    ///     </para>
    /// </summary>
    private async Task MoveWatermarksAsync(Guid executionId, Guid sessionId, long sequence, long atUtc, CancellationToken cancellationToken)
    {
        // A CASE expression rather than Math.Max: it is the shape every provider translates, and the comparison has to
        // happen in the database for the update to be atomic with respect to a concurrent writer.
        _ = await _dbContext.IntegrationExecutions.Where(row => row.Id == executionId)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastSequence,
                                    row => row.LastSequence > sequence ? row.LastSequence : sequence),
                                cancellationToken)
                            .ConfigureAwait(false);

        _ = await _dbContext.IntegrationSessions.Where(row => row.Id == sessionId)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastSequence, sequence)
                                                                  .SetProperty(row => row.LastActivityUtc, atUtc),
                                cancellationToken)
                            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The terminal event's small payload: null when the run ended without a failure, otherwise the two content-free
    ///     failure fields. Bounded by <c>FailureSummary</c>'s own length; no transcript content reaches it.
    /// </summary>
    private static byte[]? FailureDetail(string? failureCategory, string? failureSummary)
    {
        if (failureCategory is null && failureSummary is null)
        {
            return null;
        }

        return JsonSerializer.SerializeToUtf8Bytes(new IntegrationTerminalDetail(failureCategory, failureSummary));
    }

    private static string? TextOrNull(byte[]? value) =>
        value is null ? null : Encoding.UTF8.GetString(value);

    private static IntegrationExecutionSnapshot ToSnapshot(IntegrationExecution entity) =>
        new(entity.Id,
            entity.TriggerId,
            entity.SessionId,
            entity.PrincipalId,
            entity.RequestId,
            entity.RequestFingerprint,
            entity.KeyPrefix,
            entity.InvocationId,
            entity.Status,
            entity.ReceivedAtUtc,
            entity.StartedAtUtc,
            entity.EndedAtUtc,
            entity.StopRequestedAtUtc,
            entity.FailureCategory,
            entity.FailureSummary,
            entity.OutputCount,
            entity.OutputBytes,
            entity.LastSequence,
            entity.Version);

    /// <summary>
    ///     The SAME two names <c>IntegrationTerminalPayload.Failure</c> writes, so no writer — fallback or not — can put
    ///     a second failed-terminal shape in front of a reader.
    /// </summary>
    private sealed record IntegrationTerminalDetail(
        [property: JsonPropertyName("category")]
        string? Category,
        [property: JsonPropertyName("summary")]
        string? Summary);
}
