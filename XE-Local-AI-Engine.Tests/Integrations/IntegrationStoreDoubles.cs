namespace XE_Local_AI_Engine.Tests.Integrations;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     In-memory <see cref="IIntegrationApiKeyStore" />. Real behaviour rather than a mock because the key service's
///     interesting properties — a digest that never round-trips, revocation that keeps the row, many keys sharing a
///     principal — are all statements about what the store ends up holding.
/// </summary>
internal sealed class FakeIntegrationApiKeyStore : IIntegrationApiKeyStore
{
    private readonly List<IntegrationApiKeySnapshot> _rows = [];

    /// <summary>Every row as stored, newest last. The suite reads it to assert the plaintext never landed anywhere.</summary>
    public IReadOnlyList<IntegrationApiKeySnapshot> Rows => _rows;

    public Task<IntegrationApiKeySnapshot> CreateAsync(IntegrationApiKeyCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var snapshot = new IntegrationApiKeySnapshot(command.KeyId,
            command.PrincipalId,
            command.KeyPrefix,
            command.KeyHash,
            command.Label,
            command.AllowedTriggerIdsJson,
            CreatedAtUtc: 1,
            LastUsedAtUtc: null,
            RevokedAtUtc: null);
        _rows.Add(snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<IntegrationApiKeySnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IntegrationApiKeySnapshot>>(_rows.ToArray());

    public Task<IntegrationApiKeySnapshot?> GetByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(row => string.Equals(row.KeyPrefix, keyPrefix, StringComparison.Ordinal)));

    public Task<bool> TouchLastUsedAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(Replace(keyId, row => row with
        {
            LastUsedAtUtc = atUtc
        }));

    public Task<bool> RevokeAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(Replace(keyId, row => row with
        {
            RevokedAtUtc = atUtc
        }));

    private bool Replace(Guid keyId, Func<IntegrationApiKeySnapshot, IntegrationApiKeySnapshot> mutate)
    {
        var index = _rows.FindIndex(row => row.Id == keyId);
        if (index < 0)
        {
            return false;
        }

        _rows[index] = mutate(_rows[index]);
        return true;
    }
}

/// <summary>
///     In-memory <see cref="IIntegrationTriggerStore" />. <see cref="CreateAsync" /> enforces the unique name the real
///     schema does, so the service's duplicate-name path is exercised against the same failure the database produces.
/// </summary>
internal sealed class FakeIntegrationTriggerStore : IIntegrationTriggerStore
{
    private readonly List<IntegrationTriggerSnapshot> _rows = [];

    public IReadOnlyList<IntegrationTriggerSnapshot> Rows => _rows;

    /// <summary>
    ///     Makes the next <see cref="GetByNameAsync" /> answer "no such name" whatever the rows say, so a suite can
    ///     drive the window between the service's pre-check and its insert — the window the unique index closes.
    /// </summary>
    public bool HideNextNameLookup { get; set; }

    public Task<IntegrationTriggerSnapshot> CreateAsync(IntegrationTriggerCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_rows.Any(row => string.Equals(row.Name, command.Name, StringComparison.Ordinal)))
        {
            // The real schema answers a duplicate name with a unique-index violation, which surfaces as this type.
            throw new DbUpdateException($"Duplicate trigger name '{command.Name}'.");
        }

        var snapshot = new IntegrationTriggerSnapshot(command.TriggerId,
            command.Name,
            command.DisplayName,
            command.Description,
            command.Enabled,
            command.TargetKind,
            command.TargetAgentDefinitionId,
            command.SessionPolicy,
            command.AcceptedInputKinds,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Version: 1);
        _rows.Add(snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<bool> UpdateAsync(IntegrationTriggerUpdateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var index = _rows.FindIndex(row => row.Id == command.TriggerId);
        if (index < 0 || _rows[index].Version != command.ExpectedVersion)
        {
            return Task.FromResult(false);
        }

        _rows[index] = _rows[index] with
        {
            DisplayName = command.DisplayName,
            Description = command.Description,
            Enabled = command.Enabled,
            TargetAgentDefinitionId = command.TargetAgentDefinitionId,
            SessionPolicy = command.SessionPolicy,
            AcceptedInputKinds = command.AcceptedInputKinds,
            UpdatedAtUtc = 2,
            Version = command.ExpectedVersion + 1
        };
        return Task.FromResult(true);
    }

    public Task<IntegrationTriggerSnapshot?> GetByIdAsync(Guid triggerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(row => row.Id == triggerId));

    public Task<IntegrationTriggerSnapshot?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (HideNextNameLookup)
        {
            HideNextNameLookup = false;
            return Task.FromResult<IntegrationTriggerSnapshot?>(null);
        }

        return Task.FromResult(_rows.SingleOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IntegrationTriggerSnapshot>>(_rows.OrderBy(row => row.Name, StringComparer.Ordinal).ToArray());

    public Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default)
    {
        var index = _rows.FindIndex(row => row.Id == triggerId);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        _rows.RemoveAt(index);
        return Task.FromResult(true);
    }

    /// <summary>Seeds a row directly, so a suite can arrange a trigger without going through the service.</summary>
    public IntegrationTriggerSnapshot Seed(string name,
        Guid agentDefinitionId,
        bool enabled = true,
        IntegrationSessionPolicy sessionPolicy = IntegrationSessionPolicy.PerInvocation,
        IntegrationInputKinds acceptedInputKinds = IntegrationInputKinds.Text | IntegrationInputKinds.Json)
    {
        var snapshot = new IntegrationTriggerSnapshot(Guid.NewGuid(),
            name,
            name,
            Description: null,
            enabled,
            IntegrationTargetKind.Agent,
            agentDefinitionId,
            sessionPolicy,
            acceptedInputKinds,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Version: 1);
        _rows.Add(snapshot);
        return snapshot;
    }

    /// <summary>Disables a seeded trigger, which the accept path and the coordinator both treat as "no such trigger".</summary>
    public void Disable(Guid triggerId)
    {
        var index = _rows.FindIndex(row => row.Id == triggerId);
        _rows[index] = _rows[index] with
        {
            Enabled = false
        };
    }
}

/// <summary>
///     In-memory <see cref="IIntegrationExecutionStore" />. It reproduces the three behaviours the accept path depends
///     on — the revocation re-read, both admission caps, and the <c>(PrincipalId, RequestId)</c> uniqueness — because
///     a mock that merely records calls could not tell a correct accept order from a wrong one.
/// </summary>
internal sealed class FakeIntegrationExecutionStore : IIntegrationExecutionStore
{
    private readonly List<IntegrationExecutionEventSnapshot> _events = [];
    private readonly List<IntegrationExecutionSnapshot> _rows = [];

    public IReadOnlyList<IntegrationExecutionSnapshot> Rows => _rows;

    public IReadOnlyList<IntegrationExecutionEventSnapshot> Events => _events;

    /// <summary>Sessions an accept created, so a suite can assert exactly one was written per admitted execution.</summary>
    public List<IntegrationSessionCreate> CreatedSessions { get; } = [];

    /// <summary>Key prefixes the in-transaction re-read reports as revoked, which is a <see langword="false" /> and nothing written.</summary>
    public HashSet<string> RevokedKeyPrefixes { get; } = new(StringComparer.Ordinal);

    /// <summary>Makes the next accept fail the unique index, so the (PrincipalId, RequestId) race path can be driven.</summary>
    public bool FailNextAcceptWithUniqueViolation { get; set; }

    /// <summary>
    ///     Makes the next dedup lookup answer "no such request", which is what a loser of the race sees: its pre-check
    ///     runs before the winner commits, so only the unique index can decide it.
    /// </summary>
    public bool HideNextRequestIdLookup { get; set; }

    public Task<bool> AcceptAsync(IntegrationAcceptCommand command,
        int maxActive,
        int maxActivePerPrincipal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (RevokedKeyPrefixes.Contains(command.KeyPrefix))
        {
            return Task.FromResult(false);
        }

        if (FailNextAcceptWithUniqueViolation)
        {
            FailNextAcceptWithUniqueViolation = false;
            throw new DbUpdateException("UNIQUE constraint failed: integration_executions.principal_id, integration_executions.request_id");
        }

        var active = _rows.Where(static row => row.Status is IntegrationExecutionStatus.Accepted
                                               or IntegrationExecutionStatus.Queued
                                               or IntegrationExecutionStatus.Running)
                          .ToArray();
        if (active.Length >= maxActive || active.Count(row => row.PrincipalId == command.PrincipalId) >= maxActivePerPrincipal)
        {
            throw new IntegrationQueueFullException("The node is at its concurrent execution limit.");
        }

        if (command.NewSession is { } session)
        {
            CreatedSessions.Add(session);
        }

        _rows.Add(new IntegrationExecutionSnapshot(command.ExecutionId,
            command.TriggerId,
            command.SessionId,
            command.PrincipalId,
            command.RequestId,
            command.RequestFingerprint,
            command.KeyPrefix,
            InvocationId: Guid.Empty,
            IntegrationExecutionStatus.Accepted,
            command.ReceivedAtUtc,
            StartedAtUtc: null,
            EndedAtUtc: null,
            StopRequestedAtUtc: null,
            FailureCategory: null,
            FailureSummary: null,
            OutputCount: 0,
            OutputBytes: 0,
            command.AcceptedEvent.Sequence,
            Version: 0));
        AddEvent(command.AcceptedEvent);
        return Task.FromResult(true);
    }

    public Task<IntegrationExecutionSnapshot?> GetByIdAsync(Guid executionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(row => row.Id == executionId));

    /// <summary>
    ///     Inserts a row an earlier accept would have committed, so a suite can start from any point of the state
    ///     machine without replaying the whole admission path.
    /// </summary>
    public IntegrationExecutionSnapshot Seed(Guid executionId,
        Guid triggerId,
        Guid sessionId,
        IntegrationExecutionStatus status = IntegrationExecutionStatus.Accepted,
        long receivedAtUtc = 0,
        long lastSequence = 1,
        long version = 0,
        long? stopRequestedAtUtc = null,
        string keyPrefix = "xeint_abcdefgh")
    {
        var snapshot = new IntegrationExecutionSnapshot(executionId,
            triggerId,
            sessionId,
            PrincipalId: Guid.NewGuid(),
            RequestId: Guid.NewGuid(),
            RequestFingerprint: ReadOnlyMemory<byte>.Empty,
            keyPrefix,
            InvocationId: Guid.Empty,
            status,
            receivedAtUtc,
            StartedAtUtc: null,
            EndedAtUtc: null,
            stopRequestedAtUtc,
            FailureCategory: null,
            FailureSummary: null,
            OutputCount: 0,
            OutputBytes: 0,
            lastSequence,
            version);
        _rows.Add(snapshot);
        return snapshot;
    }

    public Task<IntegrationExecutionSnapshot?> GetByRequestIdAsync(Guid principalId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (HideNextRequestIdLookup)
        {
            HideNextRequestIdLookup = false;
            return Task.FromResult<IntegrationExecutionSnapshot?>(null);
        }

        return Task.FromResult(_rows.SingleOrDefault(row => row.PrincipalId == principalId && row.RequestId == requestId));
    }

    public Task<IReadOnlyList<IntegrationExecutionSnapshot>> ListAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var matches = _rows.Where(row => (filter.TriggerId is null || row.TriggerId == filter.TriggerId)
                                         && (filter.SessionId is null || row.SessionId == filter.SessionId)
                                         && (filter.Status is null || row.Status == filter.Status))
                           .OrderByDescending(static row => row.ReceivedAtUtc)
                           .ThenByDescending(static row => row.Id)
                           .Skip(filter.Offset)
                           .Take(filter.Limit)
                           .ToArray();
        return Task.FromResult<IReadOnlyList<IntegrationExecutionSnapshot>>(matches);
    }

    /// <summary>Makes the next non-terminal CAS lose, as it does when a concurrent cancel CASed the same version first.</summary>
    public bool FailNextStatusCas { get; set; }

    public Task<bool> UpdateStatusAsync(IntegrationExecutionStatusUpdate command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (FailNextStatusCas)
        {
            FailNextStatusCas = false;
            return Task.FromResult(false);
        }

        var index = _rows.FindIndex(row => row.Id == command.ExecutionId);
        if (index < 0 || _rows[index].Version != command.ExpectedVersion || !command.ExpectedStatuses.Contains(_rows[index].Status))
        {
            return Task.FromResult(false);
        }

        _rows[index] = _rows[index] with
        {
            Status = command.NewStatus,
            StartedAtUtc = command.StartedAtUtc ?? _rows[index].StartedAtUtc,
            EndedAtUtc = command.EndedAtUtc ?? _rows[index].EndedAtUtc,
            InvocationId = command.InvocationId ?? _rows[index].InvocationId,
            StopRequestedAtUtc = command.StopRequestedAtUtc ?? _rows[index].StopRequestedAtUtc,
            FailureCategory = command.FailureCategory ?? _rows[index].FailureCategory,
            FailureSummary = command.FailureSummary ?? _rows[index].FailureSummary,
            Version = _rows[index].Version + 1
        };
        return Task.FromResult(true);
    }

    /// <summary>Makes the next terminal transaction throw, which must publish nothing and leave the row non-terminal.</summary>
    public bool ThrowOnNextTerminalize { get; set; }

    /// <summary>Makes the next terminal CAS lose, as it does when another path terminalized the row first.</summary>
    public bool FailNextTerminalizeCas { get; set; }

    /// <summary>Makes the bounded retry lose as well, so a caller cannot recover by re-reading the row's version.</summary>
    public bool FailSecondTerminalizeCas { get; set; }

    /// <summary>Moves a row's receive stamp, so a queue-age deadline can be driven without waiting real minutes.</summary>
    public void Backdate(Guid executionId, long receivedAtUtc)
    {
        var index = _rows.FindIndex(row => row.Id == executionId);
        _rows[index] = _rows[index] with
        {
            ReceivedAtUtc = receivedAtUtc
        };
    }

    public Task<bool> TryTerminalizeAsync(IntegrationTerminalizeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (ThrowOnNextTerminalize)
        {
            ThrowOnNextTerminalize = false;
            throw new DbUpdateException("The terminal transaction failed.");
        }

        if (FailNextTerminalizeCas)
        {
            FailNextTerminalizeCas = FailSecondTerminalizeCas;
            FailSecondTerminalizeCas = false;
            return Task.FromResult(false);
        }

        var index = _rows.FindIndex(row => row.Id == command.ExecutionId);
        if (index < 0 || _rows[index].Version != command.ExpectedVersion || !command.ExpectedStatuses.Contains(_rows[index].Status))
        {
            return Task.FromResult(false);
        }

        _rows[index] = _rows[index] with
        {
            Status = command.NewStatus,
            EndedAtUtc = command.EndedAtUtc,
            FailureCategory = command.FailureCategory,
            FailureSummary = command.FailureSummary,
            LastSequence = Math.Max(_rows[index].LastSequence, command.Sequence),
            Version = _rows[index].Version + 1
        };
        AddEvent(new IntegrationEventAppend(Guid.NewGuid(), command.ExecutionId, command.Sequence, command.EventType, DetailJson: null, command.EndedAtUtc));
        return Task.FromResult(true);
    }

    public Task AppendEventAsync(IntegrationEventAppend command, CancellationToken cancellationToken = default)
    {
        AddEvent(command);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IntegrationExecutionEventSnapshot>> ListEventsAsync(Guid executionId,
        long sinceSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var matches = _events.Where(row => row.ExecutionId == executionId && row.Sequence > sinceSequence)
                             .OrderBy(static row => row.Sequence)
                             .Take(limit)
                             .ToArray();
        return Task.FromResult<IReadOnlyList<IntegrationExecutionEventSnapshot>>(matches);
    }

    private void AddEvent(IntegrationEventAppend command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _events.Add(new IntegrationExecutionEventSnapshot(command.EventId,
            command.ExecutionId,
            command.Sequence,
            command.EventType,
            command.DetailJson,
            command.OccurredAtUtc));
    }
}

/// <summary>
///     In-memory <see cref="IIntegrationSessionStore" />. Seeded directly rather than through an accept, because the
///     coordinator's suite starts from rows an earlier accept already committed.
/// </summary>
internal sealed class FakeIntegrationSessionStore : IIntegrationSessionStore
{
    private readonly List<IntegrationSessionSnapshot> _rows = [];

    public IReadOnlyList<IntegrationSessionSnapshot> Rows => _rows;

    public Task<IntegrationSessionSnapshot?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(row => row.Id == sessionId));

    public Task<bool> CloseAsync(Guid sessionId, long atUtc, CancellationToken cancellationToken = default)
    {
        var index = _rows.FindIndex(row => row.Id == sessionId);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        _rows[index] = _rows[index] with
        {
            Status = IntegrationSessionStatus.Closed,
            LastActivityUtc = atUtc
        };
        return Task.FromResult(true);
    }

    public IntegrationSessionSnapshot Seed(Guid sessionId, Guid triggerId, Guid conversationId, Guid agentDefinitionId)
    {
        var snapshot = new IntegrationSessionSnapshot(sessionId,
            triggerId,
            PrincipalId: Guid.NewGuid(),
            conversationId,
            agentDefinitionId,
            IntegrationSessionStatus.Active,
            CreatedAtUtc: 0,
            LastActivityUtc: 0,
            ExecutionCount: 1,
            LastSequence: 1);
        _rows.Add(snapshot);
        return snapshot;
    }
}
