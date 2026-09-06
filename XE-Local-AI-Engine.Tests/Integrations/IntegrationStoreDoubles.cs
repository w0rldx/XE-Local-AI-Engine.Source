namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    ///     How many key reads this store has served. The masked paths must do the SAME number of them whether the
    ///     addressed row exists or not, or the difference is a timing oracle for row existence behind two
    ///     byte-identical 404s.
    /// </summary>
    public int GetByPrefixCalls { get; private set; }

    public Task<IntegrationApiKeySnapshot?> GetByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        GetByPrefixCalls++;
        return Task.FromResult(_rows.SingleOrDefault(row => string.Equals(row.KeyPrefix, keyPrefix, StringComparison.Ordinal)));
    }

    public Task<bool> TouchLastUsedAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(Replace(keyId, row => row with
        {
            LastUsedAtUtc = atUtc
        }));

    /// <summary>Re-scopes a credential's allowlist, so a suite can prove the key row is re-read per request.</summary>
    public void Rescope(Guid keyId, string? allowedTriggerIdsJson)
    {
        var index = _rows.FindIndex(row => row.Id == keyId);
        _rows[index] = _rows[index] with
        {
            AllowedTriggerIdsJson = allowedTriggerIdsJson
        };
    }

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

    /// <summary>How many by-name reads this store has served, so a suite can pin the accept path's constant shape.</summary>
    public int NameLookups { get; private set; }

    public Task<IntegrationTriggerSnapshot?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        NameLookups++;
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

    /// <summary>Repoints a seeded trigger's session policy, which is what decides whether R4-9(a) judges its agent.</summary>
    public void SetSessionPolicy(Guid triggerId, IntegrationSessionPolicy sessionPolicy)
    {
        var index = _rows.FindIndex(row => row.Id == triggerId);
        _rows[index] = _rows[index] with
        {
            SessionPolicy = sessionPolicy
        };
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

    /// <summary>
    ///     The coordinator dispatches each execution onto its own task, so two runs reach these lists at once — which
    ///     a bare <see cref="List{T}" /> loses items to. Re-entrant, because the <see cref="BeforeNextStatusCas" />
    ///     hook writes through this same store.
    /// </summary>
    private readonly Lock _gate = new();

    public IReadOnlyList<IntegrationExecutionSnapshot> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows.ToArray();
            }
        }
    }

    public IReadOnlyList<IntegrationExecutionEventSnapshot> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

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

        lock (_gate)
        {
            if (RevokedKeyPrefixes.Contains(command.KeyPrefix))
            {
                return Task.FromResult(false);
            }

            if (FailNextAcceptWithUniqueViolation)
            {
                FailNextAcceptWithUniqueViolation = false;

                // The real AcceptAsync is raw ADO under BEGIN IMMEDIATE with no SaveChanges anywhere, so the unique index
                // surfaces as SqliteException with SQLITE_CONSTRAINT — never as the EF-only DbUpdateException.
                throw new SqliteException("UNIQUE constraint failed: integration_executions.principal_id, integration_executions.request_id",
                    errorCode: 19);
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
            else
            {
                // A continuation. The real store's session bump is scoped to the caller's own ACTIVE session and
                // abandons the transaction when it matches no row, which is the race-free backstop behind S3's gate.
                Sessions?.BumpForAccept(command.SessionId, command.PrincipalId, command.ReceivedAtUtc);
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
    }

    /// <summary>Makes the next row read throw before the run's own handler is in scope, which is what escapes ProcessOneAsync.</summary>
    public bool ThrowOnNextGetById { get; set; }

    /// <summary>
    ///     How many further reads throw. Set to the coordinator's retry budget to make a dispatch fault survive every
    ///     attempt, so the row has to be terminalized rather than left holding an admission slot until the next
    ///     restart — and the read that terminalization itself does still succeeds.
    /// </summary>
    public int ThrowOnGetByIdCount { get; set; }

    /// <summary>
    ///     Stamps a durable stop marker under the row's CURRENT version, exactly as the cancel primitive's step 1
    ///     does: a pure marker write that bumps the version without terminalizing.
    /// </summary>
    public void StampStopMarker(Guid executionId, long stopRequestedAtUtc)
    {
        lock (_gate)
        {
            var index = _rows.FindIndex(row => row.Id == executionId);
            _rows[index] = _rows[index] with
            {
                StopRequestedAtUtc = stopRequestedAtUtc,
                Version = _rows[index].Version + 1
            };
        }
    }

    /// <summary>
    ///     The session store an accept's continuation bump reaches, when a suite drives one. Null for the coordinator
    ///     suites, which only ever seed rows an earlier accept already committed.
    /// </summary>
    public FakeIntegrationSessionStore? Sessions { get; set; }

    /// <summary>Makes the next output append throw, which is the "the database is failing" half of the tool's contract.</summary>
    public bool ThrowOnNextOutputAppend { get; set; }

    /// <summary>Signalled before the output append commits, so a suite can observe what the tool has published so far.</summary>
    public TaskCompletionSource? BlockOutputAppendUntil { get; set; }

    /// <summary>
    ///     A store-side aggregate cap TIGHTER than the one the caller passes, which is the only way to drive the
    ///     in-transaction refusal: on a healthy node the tool's own pre-check always gets there first.
    /// </summary>
    public long? OutputCapOverride { get; set; }

    public Task<IntegrationExecutionSnapshot?> FindActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.FirstOrDefault(row => row.SessionId == sessionId && row.Status == IntegrationExecutionStatus.Running));
        }
    }

    /// <summary>
    ///     Reproduces the real store's in-transaction check-and-reserve: the store MEASURES the plaintext envelope and
    ///     refuses without writing anything when the aggregate cap would be exceeded. A double that merely recorded the
    ///     call could not tell a correct pre-check from a missing one.
    /// </summary>
    public async Task<bool> AppendOutputEventAsync(IntegrationEventAppend append, long maxOutputBytesPerExecution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(append);

        if (BlockOutputAppendUntil is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (ThrowOnNextOutputAppend)
            {
                ThrowOnNextOutputAppend = false;
                throw new SqliteException("database is locked", errorCode: 5);
            }

            var length = (long)Encoding.UTF8.GetByteCount(append.DetailJson ?? string.Empty);
            var index = _rows.FindIndex(row => row.Id == append.ExecutionId);
            if (index < 0)
            {
                throw new InvalidOperationException($"Integration execution '{append.ExecutionId}' does not exist.");
            }

            var row = _rows[index];
            if (row.OutputBytes + length > (OutputCapOverride ?? maxOutputBytesPerExecution))
            {
                return false;
            }

            _rows[index] = row with
            {
                OutputBytes = row.OutputBytes + length,
                OutputCount = row.OutputCount + 1,
                LastSequence = Math.Max(row.LastSequence, append.Sequence)
            };
            AddEvent(append);
            return true;
        }
    }

    /// <summary>Moves a row to a terminal status without replaying a whole run, so a suite can free a busy session.</summary>
    public void Complete(Guid executionId)
    {
        lock (_gate)
        {
            var index = _rows.FindIndex(row => row.Id == executionId);
            _rows[index] = _rows[index] with
            {
                Status = IntegrationExecutionStatus.Completed
            };
        }
    }

    /// <summary>Closes a row as Failed without touching its version, the way a racing writer's terminal transaction does.</summary>
    public void Fail(Guid executionId, string failureCategory)
    {
        lock (_gate)
        {
            var index = _rows.FindIndex(row => row.Id == executionId);
            _rows[index] = _rows[index] with
            {
                Status = IntegrationExecutionStatus.Failed,
                FailureCategory = failureCategory
            };
        }
    }

    public Task<int> CountActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Count(row => row.SessionId == sessionId
                                                      && row.Status is IntegrationExecutionStatus.Accepted
                                                          or IntegrationExecutionStatus.Queued
                                                          or IntegrationExecutionStatus.Running));
        }
    }

    public Task<IntegrationExecutionSnapshot?> GetByIdAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (ThrowOnGetByIdCount > 0)
            {
                ThrowOnGetByIdCount--;
                throw new DbUpdateException("The execution row could not be read.");
            }

            if (ThrowOnNextGetById)
            {
                ThrowOnNextGetById = false;
                throw new SqliteException("database is locked", errorCode: 5);
            }

            return Task.FromResult(_rows.SingleOrDefault(row => row.Id == executionId));
        }
    }

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
        lock (_gate)
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
    }

    public Task<IntegrationExecutionSnapshot?> GetByRequestIdAsync(Guid principalId, Guid requestId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (HideNextRequestIdLookup)
            {
                HideNextRequestIdLookup = false;
                return Task.FromResult<IntegrationExecutionSnapshot?>(null);
            }

            return Task.FromResult(_rows.SingleOrDefault(row => row.PrincipalId == principalId && row.RequestId == requestId));
        }
    }

    /// <summary>Makes every paged list throw, which is how a WHOLE startup sweep fails.</summary>
    public bool ThrowOnEveryList { get; set; }

    /// <summary>
    ///     Runs AFTER each read is taken, with the 1-based read number, so a suite can change the filtered set between
    ///     the read and the caller's pass over it.
    /// </summary>
    public Action<int>? AfterList { get; set; }

    /// <summary>How many reads have been served. The startup sweep's whole shape is "exactly one".</summary>
    public int ListCalls => Volatile.Read(ref _listCalls);

    private int _listCalls;

    public Task<IReadOnlyList<IntegrationExecutionSnapshot>> ListAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default)
    {
        if (ThrowOnEveryList)
        {
            throw new DbUpdateException("The execution rows could not be listed.");
        }

        ArgumentNullException.ThrowIfNull(filter);

        IReadOnlyList<IntegrationExecutionSnapshot> taken;
        lock (_gate)
        {
            taken = Matching(filter).OrderByDescending(static row => row.ReceivedAtUtc)
                                    .ThenByDescending(static row => row.Id)
                                    .Skip(filter.Offset)
                                    .Take(filter.Limit)
                                    .ToArray();
        }

        // Counted unconditionally: `hook?.Invoke(Increment())` skips the increment when no hook is set, and ListCalls
        // must be true for every suite, not only the ones that install one.
        var call = Interlocked.Increment(ref _listCalls);

        // Outside the lock: the hook mutates rows through the double's own public helpers, which take it themselves.
        AfterList?.Invoke(call);
        return Task.FromResult(taken);
    }

    public Task<int> CountAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_gate)
        {
            return Task.FromResult(Matching(filter).Count());
        }
    }

    /// <summary>The real store's shared filter, mirrored so the double's count and page agree the same way.</summary>
    private IEnumerable<IntegrationExecutionSnapshot> Matching(IntegrationExecutionFilter filter) =>
        _rows.Where(row => (filter.TriggerId is null || row.TriggerId == filter.TriggerId)
                           && (filter.SessionId is null || row.SessionId == filter.SessionId)
                           && (filter.Status is not { Count: > 0 } statuses || statuses.Contains(row.Status)));

    /// <summary>Makes the next non-terminal CAS lose, as it does when a concurrent cancel CASed the same version first.</summary>
    public bool FailNextStatusCas { get; set; }

    /// <summary>Makes the next non-terminal CAS throw, which is what a locked database or a disposed context looks like.</summary>
    public bool ThrowOnNextStatusCas { get; set; }

    /// <summary>Runs just before the next non-terminal CAS, so a suite can close the row inside the window the CAS loses to.</summary>
    public Action? BeforeNextStatusCas { get; set; }

    public Task<bool> UpdateStatusAsync(IntegrationExecutionStatusUpdate command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The real store throws on a cancelled token. A double that silently ignores it lets a caller pass the client's
        // RequestAborted into a write that must survive the client, and no test can tell.
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (ThrowOnNextStatusCas)
            {
                ThrowOnNextStatusCas = false;
                throw new SqliteException("database is locked", errorCode: 5);
            }

            if (BeforeNextStatusCas is { } hook)
            {
                BeforeNextStatusCas = null;
                hook();
            }

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
    }

    /// <summary>Every kind-3 audit row the terminal transactions committed, in order. Only a WON compare-and-swap adds one.</summary>
    public List<IntegrationInvocationAuditInput> Audits { get; } = [];

    /// <summary>Makes the next terminal transaction throw, which must publish nothing and leave the row non-terminal.</summary>
    public bool ThrowOnNextTerminalize { get; set; }

    /// <summary>
    ///     Runs on the writer's thread before each terminal CAS, carrying that CAS's 1-based ordinal, so a suite can
    ///     drift the row's version INSIDE the window a bounded retry has to survive — which is exactly what a caller
    ///     hammering cancel does to a coordinator that is finishing.
    /// </summary>
    public Action<int>? BeforeTerminalizeCas { get; set; }

    private int _terminalizeCalls;

    /// <summary>Moves a row's receive stamp, so a queue-age deadline can be driven without waiting real minutes.</summary>
    public void Backdate(Guid executionId, long receivedAtUtc)
    {
        lock (_gate)
        {
            var index = _rows.FindIndex(row => row.Id == executionId);
            _rows[index] = _rows[index] with
            {
                ReceivedAtUtc = receivedAtUtc
            };
        }
    }

    public Task<bool> TryTerminalizeAsync(IntegrationTerminalizeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            BeforeTerminalizeCas?.Invoke(++_terminalizeCalls);

            if (ThrowOnNextTerminalize)
            {
                ThrowOnNextTerminalize = false;
                throw new DbUpdateException("The terminal transaction failed.");
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

            // The kind-3 audit row is part of the terminal TRANSACTION now, so it lands here and only for the winner —
            // a double that dropped it would hide the very atomicity the store guarantees.
            if (command.Audit is { } audit)
            {
                Audits.Add(audit);
            }

            // The real store writes the caller's payload onto the terminal row; a double that dropped it would hide a
            // stream and a poll answering differently.
            AddEvent(new IntegrationEventAppend(Guid.NewGuid(), command.ExecutionId, command.Sequence, command.EventType, command.EventDetailJson, command.EndedAtUtc));
            return Task.FromResult(true);
        }
    }

    /// <summary>Runs on the WRITER's thread before each append, so a suite can observe where the write actually happens.</summary>
    public Action<IntegrationEventAppend>? OnAppendEvent { get; set; }

    /// <summary>
    ///     Decides which event appends throw. A predicate rather than a flag because the coordinator writes its own
    ///     phase rows through this method too, and a test about the DRAIN must not break the run before it starts.
    /// </summary>
    public Func<IntegrationEventAppend, bool>? FailAppendEventWhen { get; set; }

    public Task AppendEventAsync(IntegrationEventAppend command, CancellationToken cancellationToken = default)
    {
        OnAppendEvent?.Invoke(command);
        if (FailAppendEventWhen?.Invoke(command) == true)
        {
            throw new DbUpdateException("The event row could not be written.");
        }

        lock (_gate)
        {
            AddEvent(command);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IntegrationExecutionEventSnapshot>> ListEventsAsync(Guid executionId,
        long sinceSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (_gate)
        {
            var matches = _events.Where(row => row.ExecutionId == executionId && row.Sequence > sinceSequence)
                                 .OrderBy(static row => row.Sequence)
                                 .Take(limit)
                                 .ToArray();
            return Task.FromResult<IReadOnlyList<IntegrationExecutionEventSnapshot>>(matches);
        }
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

    public Task<IntegrationSessionSnapshot?> GetForPrincipalAsync(Guid sessionId, Guid principalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(row => row.Id == sessionId && row.PrincipalId == principalId));

    public Task<IntegrationSessionSnapshot?> FindByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.SingleOrDefault(row => row.ConversationId == conversationId));

    public Task<IReadOnlyList<IntegrationSessionSnapshot>> ListAsync(Guid? triggerId,
        IntegrationSessionStatus? status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IntegrationSessionSnapshot> page =
        [
            .. _rows.Where(row => (triggerId is not { } trigger || row.TriggerId == trigger)
                                  && (status is not { } sessionStatus || row.Status == sessionStatus))
                    .OrderByDescending(static row => row.LastActivityUtc)
                    .ThenByDescending(static row => row.Id)
                    .Skip(Math.Max(val1: 0, offset))
                    .Take(Math.Max(val1: 0, limit))
        ];
        return Task.FromResult(page);
    }

    public Task<int> CountAsync(Guid? triggerId, IntegrationSessionStatus? status, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.Count(row => (triggerId is not { } trigger || row.TriggerId == trigger)
                                           && (status is not { } sessionStatus || row.Status == sessionStatus)));

    public Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.RemoveAll(row => row.Id == sessionId) > 0);

    /// <summary>Drops a row behind the caller's back, which is how the accept transaction's backstop gets driven.</summary>
    public void Forget(Guid sessionId) =>
        _ = _rows.RemoveAll(row => row.Id == sessionId);

    /// <summary>
    ///     The accept transaction's half of a continuation: bump the caller's own ACTIVE session, or abandon. It throws
    ///     the same type the real store does, which is what lets a suite drive the backstop rather than assume it.
    /// </summary>
    public void BumpForAccept(Guid sessionId, Guid principalId, long atUtc)
    {
        var index = _rows.FindIndex(row => row.Id == sessionId
                                           && row.PrincipalId == principalId
                                           && row.Status == IntegrationSessionStatus.Active);
        if (index < 0)
        {
            throw new IntegrationSessionUnavailableException($"Integration session '{sessionId}' cannot host this execution.");
        }

        _rows[index] = _rows[index] with
        {
            ExecutionCount = _rows[index].ExecutionCount + 1,
            LastActivityUtc = atUtc
        };
    }

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

    /// <summary>Points a seeded session at a different integrator, which is what the masking rule keys on.</summary>
    public void Reassign(Guid sessionId, Guid principalId)
    {
        var index = _rows.FindIndex(row => row.Id == sessionId);
        _rows[index] = _rows[index] with
        {
            PrincipalId = principalId
        };
    }

    public IntegrationSessionSnapshot Seed(Guid sessionId,
        Guid triggerId,
        Guid conversationId,
        Guid agentDefinitionId,
        Guid? principalId = null,
        IntegrationSessionStatus status = IntegrationSessionStatus.Active,
        long lastActivityUtc = 0,
        int executionCount = 1)
    {
        var snapshot = new IntegrationSessionSnapshot(sessionId,
            triggerId,
            principalId ?? Guid.NewGuid(),
            conversationId,
            agentDefinitionId,
            status,
            CreatedAtUtc: 0,
            lastActivityUtc,
            executionCount,
            LastSequence: 1);
        _rows.Add(snapshot);
        return snapshot;
    }
}
