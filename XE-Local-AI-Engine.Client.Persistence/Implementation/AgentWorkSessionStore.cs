namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The durable substrate for agent work sessions. Every mutation takes the session row inside one transaction, which
///     is what makes the single <c>last_sequence</c> counter safe: two writers cannot allocate the same watermark, and
///     neither can skip one.
/// </summary>
internal sealed partial class AgentWorkSessionStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IAgentWorkSessionStore
{
    private static readonly IReadOnlyDictionary<AgentWorkSessionStatus, HashSet<AgentWorkSessionStatus>> LegalTransitions =
        new Dictionary<AgentWorkSessionStatus, HashSet<AgentWorkSessionStatus>>
        {
            [AgentWorkSessionStatus.Draft] = [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Cancelled],
            [AgentWorkSessionStatus.Running] =
            [
                AgentWorkSessionStatus.Paused,
                AgentWorkSessionStatus.WaitingForInput,
                AgentWorkSessionStatus.WaitingForApproval,
                AgentWorkSessionStatus.Completed,
                AgentWorkSessionStatus.Failed,
                AgentWorkSessionStatus.Cancelled,
                AgentWorkSessionStatus.Interrupted
            ],
            [AgentWorkSessionStatus.Paused] = [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Cancelled],
            [AgentWorkSessionStatus.WaitingForInput] =
                [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Cancelled, AgentWorkSessionStatus.Interrupted],
            [AgentWorkSessionStatus.WaitingForApproval] =
                [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Cancelled, AgentWorkSessionStatus.Interrupted],
            [AgentWorkSessionStatus.Interrupted] =
                [AgentWorkSessionStatus.Running, AgentWorkSessionStatus.Paused, AgentWorkSessionStatus.Failed, AgentWorkSessionStatus.Cancelled]
        };

    private static readonly HashSet<AgentWorkSessionStatus> TerminalStatuses =
        [AgentWorkSessionStatus.Completed, AgentWorkSessionStatus.Failed, AgentWorkSessionStatus.Cancelled];

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private async Task<WorkSessionMutationResult> ExecuteMutationAsync(Guid sessionId,
        long expectedVersion,
        Guid? operationId,
        Func<AgentWorkSession, Task<MutationOutcome>> mutate,
        CancellationToken cancellationToken)
    {
        // Query-first, never insert-then-catch: a caught unique-index violation leaves an Added entity in the change
        // tracker that every later write in the same scope would trip over.
        if (operationId is { } preflight && await FindOperationAsync(sessionId, preflight, cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            return recorded;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operationId is { } inTransaction && await FindOperationAsync(sessionId, inTransaction, cancellationToken).ConfigureAwait(false) is { } alreadyRecorded)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return alreadyRecorded;
            }

            var session = await _dbContext.AgentWorkSessions.SingleOrDefaultAsync(entity => entity.Id == sessionId, cancellationToken).ConfigureAwait(false)
                          ?? throw new WorkSessionNotFoundException($"Work session '{sessionId}' was not found.");
            EnsureVersion(session, expectedVersion);

            var outcome = await mutate(session).ConfigureAwait(false);
            var sequence = AddEvent(session, outcome.EventType, outcome.Outcome, operationId, outcome.DetailJson);
            session.Version++;
            session.UpdatedAtUtc = Now();
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkSessionMutationResult(sessionId, sequence, session.StepCount, session.Version, session.Status, session.CurrentTaskId, outcome.SupersededArtifactId);
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw new WorkSessionConcurrencyException("A concurrent writer won the race before the work session mutation committed.", exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     The event already recorded for this operation, rebuilt against the session row as it stands now — a replayed
    ///     step wants the version it should continue from, not the one the first attempt saw.
    /// </summary>
    private async Task<WorkSessionMutationResult?> FindOperationAsync(Guid sessionId, Guid operationId, CancellationToken cancellationToken)
    {
        var recorded = await _dbContext.AgentWorkSessionEvents.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.SessionId == sessionId && entity.OperationId == operationId, cancellationToken)
                                       .ConfigureAwait(false);
        if (recorded is null)
        {
            return null;
        }

        var session = await _dbContext.AgentWorkSessions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == sessionId, cancellationToken).ConfigureAwait(false);
        return session is null
            ? null
            : new WorkSessionMutationResult(sessionId, recorded.Sequence, recorded.Step, session.Version, session.Status, session.CurrentTaskId);
    }

    private long AddEvent(AgentWorkSession session, string eventType, string? outcome, Guid? operationId, byte[]? detailJson)
    {
        var sequence = NextSequence(session);
        _dbContext.AgentWorkSessionEvents.Add(new AgentWorkSessionEvent
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Sequence = sequence,
            Step = session.StepCount,
            EventType = eventType,
            DetailJson = detailJson,
            OperationId = operationId,
            Outcome = outcome,
            OccurredAtUtc = Now()
        });
        return sequence;
    }

    private static void ApplyStatus(AgentWorkSession session, AgentWorkSessionStatus target, Guid? currentTaskId)
    {
        if (!LegalTransitions.TryGetValue(session.Status, out var allowed) || !allowed.Contains(target))
        {
            throw new WorkSessionInvalidTransitionException($"Work session transition {session.Status} -> {target} is not legal.");
        }

        session.Status = target;
        if (TerminalStatuses.Contains(target))
        {
            session.CurrentTaskId = null;
        }
        else if (currentTaskId is { } taskId)
        {
            session.CurrentTaskId = taskId;
        }
    }

    private static long NextSequence(AgentWorkSession session) =>
        ++session.LastSequence;

    private static void EnsureVersion(AgentWorkSession session, long expectedVersion)
    {
        if (expectedVersion == WorkSessionVersions.Any || session.Version == expectedVersion)
        {
            return;
        }

        throw new WorkSessionConcurrencyException($"The work session version is stale (expected {expectedVersion}, current {session.Version}).");
    }

    private async Task RollbackAsync(IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
    }

    private static AgentWorkSessionSnapshot Snapshot(AgentWorkSession session) =>
        new(session.Id,
            session.Title,
            Text(session.Objective),
            session.Kind,
            session.Status,
            session.AgentDefinitionId,
            session.ConversationId,
            session.CurrentTaskId,
            session.StepCount,
            session.LastCheckpointId,
            session.LastSequence,
            session.ConfigVersion,
            session.CreatedAtUtc,
            session.UpdatedAtUtc,
            session.Version);

    private static WorkSessionArtifactSnapshot ArtifactSnapshot(AgentWorkSessionArtifact artifact) =>
        new(artifact.Id,
            artifact.SessionId,
            artifact.Sequence,
            artifact.Kind,
            artifact.Name,
            artifact.MediaType,
            artifact.ContentSha256,
            artifact.SizeBytes,
            artifact.IsValid,
            artifact.ManagedReference,
            artifact.CreatedStep);

    private static WorkSessionCheckpointSnapshot CheckpointSnapshot(AgentWorkSessionCheckpoint checkpoint) =>
        new(checkpoint.Id,
            checkpoint.SessionId,
            checkpoint.Sequence,
            checkpoint.Step,
            TextOrNull(checkpoint.Summary),
            Text(checkpoint.StateJson),
            checkpoint.CreatedAtUtc);

    private static byte[]? ReasonDetail(string? sanitizedReason) =>
        string.IsNullOrWhiteSpace(sanitizedReason) ? null : Utf8(JsonSerializer.Serialize(new ReasonDetailPayload(sanitizedReason)));

    private static void EnsureNotBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
        }
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static byte[] Utf8(string value) =>
        Encoding.UTF8.GetBytes(value);

    private static byte[]? Utf8OrNull(string? value) =>
        value is null ? null : Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) =>
        Encoding.UTF8.GetString(value);

    private static string? TextOrNull(byte[]? value) =>
        value is null ? null : Encoding.UTF8.GetString(value);

    private sealed record MutationOutcome(string EventType, string? Outcome, byte[]? DetailJson, Guid? SupersededArtifactId = null);

    private sealed record ArtifactReplacementDetail(Guid SupersededArtifactId, string SupersededManagedReference);

    private sealed record ReasonDetailPayload(string Reason);
}
