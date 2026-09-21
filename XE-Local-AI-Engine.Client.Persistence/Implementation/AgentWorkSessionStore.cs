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
public sealed partial class AgentWorkSessionStore : IAgentWorkSessionStore
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

    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public AgentWorkSessionStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    private async Task<WorkSessionMutationResult> ExecuteMutationAsync(Guid sessionId,
        long expectedVersion,
        Guid? operationId,
        Func<AgentWorkSession, Task<MutationOutcome>> mutate,
        CancellationToken cancellationToken)
    {
        // Query-first, never insert-then-catch: a caught unique-index violation leaves an Added entity in the change
        // tracker that every later write in the same scope would trip over.
        if (operationId is { } preflight && await FindOperationAsync(sessionId, preflight, cancellationToken) is { } recorded)
        {
            return recorded;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (operationId is { } inTransaction && await FindOperationAsync(sessionId, inTransaction, cancellationToken) is { } alreadyRecorded)
            {
                await transaction.CommitAsync(cancellationToken);
                return alreadyRecorded;
            }

            var session = await _dbContext.AgentWorkSessions.SingleOrDefaultAsync(entity => entity.Id == sessionId, cancellationToken)
                          ?? throw new WorkSessionNotFoundException($"Work session '{sessionId}' was not found.");
            EnsureVersion(session, expectedVersion);

            var outcome = await mutate(session);
            var sequence = AddEvent(session, outcome.EventType, outcome.Outcome, operationId, outcome.DetailJson);
            session.Version++;
            session.UpdatedAtUtc = Now();
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WorkSessionMutationResult { SessionId = sessionId, Sequence = sequence, Step = session.StepCount, Version = session.Version, Status = session.Status, CurrentTaskId = session.CurrentTaskId, SupersededArtifactId = outcome.SupersededArtifactId };
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction);
            throw new WorkSessionConcurrencyException("A concurrent writer won the race before the work session mutation committed.", exception);
        }
        catch
        {
            await RollbackAsync(transaction);
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
                                       .SingleOrDefaultAsync(entity => entity.SessionId == sessionId && entity.OperationId == operationId, cancellationToken);
        if (recorded is null)
        {
            return null;
        }

        var session = await _dbContext.AgentWorkSessions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == sessionId, cancellationToken);
        return session is null
            ? null
            : new WorkSessionMutationResult { SessionId = sessionId, Sequence = recorded.Sequence, Step = recorded.Step, Version = session.Version, Status = session.Status, CurrentTaskId = session.CurrentTaskId };
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
        await transaction.RollbackAsync(CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
    }

    private static AgentWorkSessionSnapshot Snapshot(AgentWorkSession session) =>
        new()
        {
            Id = session.Id,
            Title = session.Title,
            Objective = Text(session.Objective),
            Kind = session.Kind,
            Status = session.Status,
            AgentDefinitionId = session.AgentDefinitionId,
            ConversationId = session.ConversationId,
            CurrentTaskId = session.CurrentTaskId,
            StepCount = session.StepCount,
            LastCheckpointId = session.LastCheckpointId,
            LastSequence = session.LastSequence,
            ConfigVersion = session.ConfigVersion,
            CreatedAtUtc = session.CreatedAtUtc,
            UpdatedAtUtc = session.UpdatedAtUtc,
            Version = session.Version
        };

    private static WorkSessionArtifactSnapshot ArtifactSnapshot(AgentWorkSessionArtifact artifact) =>
        new()
        {
            Id = artifact.Id,
            SessionId = artifact.SessionId,
            Sequence = artifact.Sequence,
            Kind = artifact.Kind,
            Name = artifact.Name,
            MediaType = artifact.MediaType,
            ContentSha256 = artifact.ContentSha256,
            SizeBytes = artifact.SizeBytes,
            IsValid = artifact.IsValid,
            ManagedReference = artifact.ManagedReference,
            CreatedStep = artifact.CreatedStep
        };

    private static WorkSessionCheckpointSnapshot CheckpointSnapshot(AgentWorkSessionCheckpoint checkpoint) =>
        new()
        {
            Id = checkpoint.Id,
            SessionId = checkpoint.SessionId,
            Sequence = checkpoint.Sequence,
            Step = checkpoint.Step,
            Summary = TextOrNull(checkpoint.Summary),
            StateJson = Text(checkpoint.StateJson),
            CreatedAtUtc = checkpoint.CreatedAtUtc
        };

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

    private sealed record MutationOutcome
    {
        public required string EventType { get; init; }

        public required string? Outcome { get; init; }

        public required byte[]? DetailJson { get; init; }

        public Guid? SupersededArtifactId { get; init; }
    }

    private sealed record ArtifactReplacementDetail(Guid SupersededArtifactId, string SupersededManagedReference);

    private sealed record ReasonDetailPayload(string Reason);
}
