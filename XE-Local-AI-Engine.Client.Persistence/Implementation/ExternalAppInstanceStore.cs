namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for installed external applications and their event feed.
///     <para>
///         Every method goes through EF so both encryption interceptors run — <c>variables_json</c> and
///         <c>bridge_token</c> are encrypted columns, and only a <c>SaveChanges</c> seals them, only a materialization
///         opens them. The compare-and-swap idiom
///         is <c>IntegrationExecutionStore</c>'s, verbatim: query the tracked row, compare <c>Version</c> and the
///         expected status set, mutate, <c>Version++</c>, save; a <see cref="DbUpdateConcurrencyException" /> is the
///         loser learning it lost, and any other exception clears the tracker before it propagates so the next call on
///         this scoped context cannot replay the failed one's changes.
///     </para>
/// </summary>
public sealed class ExternalAppInstanceStore : IExternalAppInstanceStore
{
    /// <summary>
    ///     The brief's bound on an event's detail, in UTF-8 bytes. There is no exempt kind here: unlike the integration
    ///     family's <c>external.output</c>, no event on this feed carries a caller payload — service names, exit codes
    ///     and failure categories all fit inside it many times over.
    /// </summary>
    private const int MaxEventDetailBytes = 4096;

    private readonly NodeChatDbContext _dbContext;

    public ExternalAppInstanceStore(NodeChatDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<ExternalAppInstanceSnapshot?> GetAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ExternalAppInstances.AsNoTracking().SingleOrDefaultAsync(row => row.Id == instanceId, cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IReadOnlyList<ExternalAppInstanceSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        // The Id tie-break is not decoration: InstalledAtUtc is a millisecond stamp, and two installs in the same
        // millisecond would otherwise order non-deterministically between reads of the same unchanged table.
        var entities = await _dbContext.ExternalAppInstances.AsNoTracking()
                                       .OrderByDescending(row => row.InstalledAtUtc)
                                       .ThenByDescending(row => row.Id)
                                       .ToListAsync(cancellationToken);
        return [.. entities.Select(ToSnapshot)];
    }

    public async Task<IReadOnlyList<ExternalAppInstanceSnapshot>> ListByApplicationAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        var entities = await _dbContext.ExternalAppInstances.AsNoTracking()
                                       .Where(row => row.ApplicationId == applicationId)
                                       .OrderByDescending(row => row.InstalledAtUtc)
                                       .ThenByDescending(row => row.Id)
                                       .ToListAsync(cancellationToken);
        return [.. entities.Select(ToSnapshot)];
    }

    public async Task<ExternalAppStatusWriteResult> CreateAsync(ExternalAppInstanceCreate command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireEventDetailWithinBounds(command.FirstEventDetailJson);

        const long FirstSequence = 1;

        _ = _dbContext.ExternalAppInstances.Add(new ExternalAppInstance
        {
            Id = command.Id,
            ApplicationId = command.ApplicationId,
            ManifestVersion = command.ManifestVersion,
            ManifestSnapshotJson = command.ManifestSnapshotJson,
            DisplayName = command.DisplayName,
            Status = ExternalAppInstanceStatus.Installing,
            DesiredState = ExternalAppDesiredState.Stopped,
            RuntimeOverride = command.RuntimeOverride,
            RuntimeProvider = command.RuntimeProvider,
            VariablesJson = Encoding.UTF8.GetBytes(command.VariablesJson),
            BridgeToken = command.BridgeToken is null ? null : Encoding.UTF8.GetBytes(command.BridgeToken),
            PublishedPortsJson = "{}",
            StoragePath = command.StoragePath,
            NeedsRecreate = false,
            InstalledAtUtc = command.CreatedAtUtc,
            UpdatedAtUtc = command.CreatedAtUtc,
            LastSequence = FirstSequence,
            Version = 0
        });

        _ = _dbContext.ExternalAppInstanceEvents.Add(new ExternalAppInstanceEvent
        {
            Id = Guid.NewGuid(),
            InstanceId = command.Id,
            Sequence = FirstSequence,
            Kind = command.FirstEventKind,
            DetailJson = command.FirstEventDetailJson,
            OccurredAtUtc = command.CreatedAtUtc
        });

        // One SaveChanges is one transaction, and both rows are in it: an instance without its first event would leave
        // the feed starting at sequence 2 with nothing recording the accepted permissions.
        await SaveOrClearAsync(cancellationToken);

        return new ExternalAppStatusWriteResult(Applied: true, FirstSequence, Version: 0);
    }

    public async Task<ExternalAppStatusWriteResult> UpdateStatusAsync(ExternalAppStatusUpdate command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireEventDetailWithinBounds(command.EventDetailJson);

        // FIRST, before the read this write mutates. Acquiring it afterwards put the transaction outside the boundary
        // that clears the tracker: a failure to begin one then left the entity mutated and the event queued on this
        // scoped context, and the next save on it — a different caller's — would have committed this transition.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var entity = await _dbContext.ExternalAppInstances.SingleOrDefaultAsync(row => row.Id == command.InstanceId, cancellationToken);
        if (entity is null || entity.Version != command.ExpectedVersion || !command.ExpectedStatuses.Contains(entity.Status))
        {
            return Lost;
        }

        entity.Status = command.NewStatus;

        // A null optional field means "leave it alone", never "clear it".
        if (command.DesiredState is { } desiredState)
        {
            entity.DesiredState = desiredState;
        }

        if (command.PublishedPortsJson is { } publishedPortsJson)
        {
            entity.PublishedPortsJson = publishedPortsJson;
        }

        if (command.ManifestSnapshotJson is { } manifestSnapshotJson)
        {
            entity.ManifestSnapshotJson = manifestSnapshotJson;
        }

        if (command.ManifestVersion is { } manifestVersion)
        {
            entity.ManifestVersion = manifestVersion;
        }

        if (command.RuntimeProvider is { } runtimeProvider)
        {
            entity.RuntimeProvider = runtimeProvider;
        }

        if (command.StartedAtUtc is { } startedAtUtc)
        {
            entity.StartedAtUtc = startedAtUtc;
        }

        if (command.StoppedAtUtc is { } stoppedAtUtc)
        {
            entity.StoppedAtUtc = stoppedAtUtc;
        }

        if (command.NeedsRecreate is { } needsRecreate)
        {
            entity.NeedsRecreate = needsRecreate;
        }

        // ASSIGNED rather than merged, and the one exception to the rule above: a transition is the newest word on why
        // the instance is where it is, so a successful start carrying two nulls must not inherit the category a failed
        // attempt left behind. ClearFailure says that out loud and wins over any values passed beside it.
        entity.FailureCategory = command.ClearFailure ? null : command.FailureCategory;
        entity.FailureSummary = command.ClearFailure ? null : command.FailureSummary;

        entity.LastSequence++;
        entity.UpdatedAtUtc = command.OccurredAtUtc;
        entity.Version++;

        var sequence = entity.LastSequence;
        var version = entity.Version;

        _ = _dbContext.ExternalAppInstanceEvents.Add(new ExternalAppInstanceEvent
        {
            Id = Guid.NewGuid(),
            InstanceId = command.InstanceId,
            Sequence = sequence,
            Kind = command.EventKind,
            DetailJson = command.EventDetailJson,
            OccurredAtUtc = command.OccurredAtUtc
        });

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The query-first check alone is not atomic; the version's concurrency-token mapping is what makes two
            // concurrent compare-and-swaps resolve to exactly one winner, and the loser learns it lost here.
            _dbContext.ChangeTracker.Clear();
            return Lost;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // The SAME loser, reported by the other half of the batch. This store MINTS the sequence, so the loser's
            // event collides on ux_external_app_instance_events_instance_sequence and SQLite raises that before EF
            // gets to count the rows its UPDATE did not affect. It is not a caller bug the way a duplicate sequence is
            // in the integration family: the only way to reach a sequence already taken is for another writer to have
            // won the compare-and-swap this one just lost, and it would have had to bump the version to do it.
            _dbContext.ChangeTracker.Clear();
            return Lost;
        }
        catch (Exception)
        {
            // EVERYTHING inside the transaction, not only the save: a commit that throws rolls the database back while
            // EF still holds the mutated entity as committed, and the next compare-and-swap on this scoped context
            // would then contend against that stale identity-map version and lose forever.
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        return new ExternalAppStatusWriteResult(Applied: true, sequence, version);
    }

    public async Task<bool> UpdateVariablesAsync(Guid instanceId,
        long expectedVersion,
        string variablesJson,
        long updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variablesJson);

        var entity = await _dbContext.ExternalAppInstances.SingleOrDefaultAsync(row => row.Id == instanceId, cancellationToken);
        if (entity is null || entity.Version != expectedVersion)
        {
            return false;
        }

        entity.VariablesJson = Encoding.UTF8.GetBytes(variablesJson);

        // Always, never "only when the values differ": the containers were created with the old environment and a
        // created container's environment is immutable, so the row has to record that a rebuild is owed.
        entity.NeedsRecreate = true;
        entity.UpdatedAtUtc = updatedAtUtc;
        entity.Version++;

        return await SaveCasAsync(cancellationToken);
    }

    public async Task<ExternalAppStatusWriteResult> CommitUpdateAsync(Guid instanceId,
        long expectedVersion,
        string manifestSnapshotJson,
        string variablesJson,
        string publishedPortsJson,
        int manifestVersion,
        long updatedAtUtc,
        string? bridgeToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestSnapshotJson);
        ArgumentNullException.ThrowIfNull(variablesJson);
        ArgumentNullException.ThrowIfNull(publishedPortsJson);

        var entity = await _dbContext.ExternalAppInstances.SingleOrDefaultAsync(row => row.Id == instanceId, cancellationToken);
        if (entity is null || entity.Version != expectedVersion)
        {
            return Lost;
        }

        // No status, no desired state and no event: this is the update's recovery boundary, not a transition. The
        // columns move together or not at all, so a crash can only leave the instance wholly on the old manifest or
        // wholly on the new one.
        entity.ManifestSnapshotJson = manifestSnapshotJson;
        entity.VariablesJson = Encoding.UTF8.GetBytes(variablesJson);
        entity.PublishedPortsJson = publishedPortsJson;
        entity.ManifestVersion = manifestVersion;
        entity.UpdatedAtUtc = updatedAtUtc;

        if (bridgeToken is not null)
        {
            // The backfill, inside the same transaction as everything else this commit moves: the replacement
            // containers were created with this token, so a row that kept its null would hand the next Start a
            // grant the running containers do not have.
            entity.BridgeToken = Encoding.UTF8.GetBytes(bridgeToken);
        }

        entity.Version++;

        var sequence = entity.LastSequence;
        var version = entity.Version;

        return await SaveCasAsync(cancellationToken)
            ? new ExternalAppStatusWriteResult(Applied: true, sequence, version)
            : Lost;
    }

    public async Task<IReadOnlyList<ExternalAppInstanceEventSnapshot>> ListEventsAsync(Guid instanceId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "An event page limit must be positive.");
        }

        // Materialize the entities FIRST and map afterwards, the shape every store in this file's family uses:
        // projecting the columns inside the LINQ query materializes no entity, so the decryption interceptor never
        // runs — harmless while detail_json is plaintext, and silently wrong the day a column here becomes encrypted.
        var events = await _dbContext.ExternalAppInstanceEvents.AsNoTracking()
                                     .Where(row => row.InstanceId == instanceId && row.Sequence > afterSequence)
                                     .OrderBy(row => row.Sequence)
                                     .Take(limit)
                                     .ToListAsync(cancellationToken);

        return
        [
            .. events.Select(static row => new ExternalAppInstanceEventSnapshot(row.Id, row.InstanceId, row.Sequence, row.Kind, row.DetailJson, row.OccurredAtUtc))
        ];
    }

    public async Task<bool> DeleteAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // The compare-and-swap is the WHERE clause here rather than a tracked entity, which is what makes this the
            // one write in the store with no change tracker in it. Tracking the row instead was the bug this shape
            // replaces: EF cascades a tracked principal's deletion onto the dependents it happens to be tracking, so
            // the events this method had already bulk-deleted were queued for a second DELETE that affected no rows,
            // and a perfectly good uninstall reported itself a lost CAS.
            var deleted = await _dbContext.ExternalAppInstances.Where(row => row.Id == instanceId && row.Version == expectedVersion)
                                          .ExecuteDeleteAsync(cancellationToken);
            if (deleted == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // Explicitly, and inside the same transaction as the row: cascades never fire on this connection, so an
            // events delete left to the database would leave every event of this instance behind forever.
            _ = await _dbContext.ExternalAppInstanceEvents.Where(row => row.InstanceId == instanceId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception)
        {
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        // The row is gone; a copy of it left in the identity map would answer the next read on this scoped context.
        _dbContext.ChangeTracker.Clear();
        return true;
    }

    /// <summary>A lost compare-and-swap: nothing written, no sequence minted, no version to report.</summary>
    private static ExternalAppStatusWriteResult Lost => new(Applied: false, Sequence: 0, Version: 0);

    /// <summary>
    ///     SQLite result code 19 is <c>SQLITE_CONSTRAINT</c>; the only unique constraint any write here can reach is
    ///     the event feed's <c>(instance_id, sequence)</c>.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    /// <summary>
    ///     The single-save compare-and-swap tail shared by <see cref="UpdateVariablesAsync" /> and
    ///     <see cref="CommitUpdateAsync" />: one <c>SaveChanges</c> is already one transaction, so there is no second
    ///     write to bracket — only the same two failure rules.
    /// </summary>
    private async Task<bool> SaveCasAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            return false;
        }
        catch (Exception)
        {
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        return true;
    }

    /// <summary>
    ///     One insert-only save. A concurrency exception is impossible here — nothing is being compared — so a failure
    ///     is a real one and propagates, with the tracker cleared first for the reason
    ///     <see cref="SaveCasAsync" /> documents.
    /// </summary>
    private async Task SaveOrClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception)
        {
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    ///     The 4 KiB bound, measured in UTF-8 bytes rather than characters, because the column stores bytes and a
    ///     character count would admit four times the payload on a non-ASCII detail.
    /// </summary>
    private static void RequireEventDetailWithinBounds(string? detailJson)
    {
        if (detailJson is { } detail && Encoding.UTF8.GetByteCount(detail) > MaxEventDetailBytes)
        {
            throw new ArgumentException($"An external app instance event's detail may not exceed {MaxEventDetailBytes} UTF-8 bytes.", nameof(detailJson));
        }
    }

    private static ExternalAppInstanceSnapshot ToSnapshot(ExternalAppInstance entity) =>
        new(entity.Id,
            entity.ApplicationId,
            entity.ManifestVersion,
            entity.ManifestSnapshotJson,
            entity.DisplayName,
            entity.Status,
            entity.DesiredState,
            entity.RuntimeOverride,
            entity.RuntimeProvider,
            Encoding.UTF8.GetString(entity.VariablesJson),
            entity.PublishedPortsJson,
            entity.StoragePath,
            entity.FailureCategory,
            entity.FailureSummary,
            entity.NeedsRecreate,
            entity.InstalledAtUtc,
            entity.StartedAtUtc,
            entity.StoppedAtUtc,
            entity.UpdatedAtUtc,
            entity.LastSequence,
            entity.Version,
            entity.BridgeToken is null ? null : Encoding.UTF8.GetString(entity.BridgeToken));
}
