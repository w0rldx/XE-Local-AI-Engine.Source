namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Ages out whole conversations once they pass the configured retention window, deleting the same footprint the
///     interactive immediate purge does.
/// </summary>
/// <remarks>
///     That footprint is every child DB row, via <see cref="INodeRetentionStore" />, plus the on-disk upload blobs and
///     the artifact bytes of any work session the conversation owned. Retention permanently destroys user chat
///     history, so it is <b>disabled by default</b> — see <see cref="ChatRetentionOptions" />. The rows are committed
///     first and the blobs torn down after, and an orphan resweep each pass removes any upload directory whose
///     conversation row is gone, so an interruption can never leave a permanent orphan.
/// </remarks>
public sealed class RetentionSweeperService : BackgroundService
{
    /// <summary>How long an artifact scope directory is spared after its newest write before it can be reclaimed.</summary>
    /// <remarks>
    ///     A scope directory appears only after its owning row commits, so an orphan is settled — but the sweep still
    ///     reads the disk and the database a moment apart, and it deletes recursively. The window is cheap insurance,
    ///     comfortably longer than any artifact write, and matches the one the knowledge blob store gives its own
    ///     interrupted-write siblings for the same reason.
    /// </remarks>
    private static readonly TimeSpan OrphanedScopeGrace = TimeSpan.FromMinutes(15);

    private readonly ILogger<RetentionSweeperService> _logger;
    private readonly ChatRetentionOptions _options;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly NodeChatPersistenceWriter _writer;

    public RetentionSweeperService(IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        NodeChatPersistenceWriter writer,
        IOptions<ChatRetentionOptions> options,
        ILogger<RetentionSweeperService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Chat retention is disabled; conversations are never auto-deleted. Set {Section}:Enabled=true to enable it.", ChatRetentionOptions.Section);

            // Inactivity-based deletion stays gated on Enabled, but the orphaned-upload resweep runs regardless: a
            // failed interactive purge can strand a directory that nothing else would reconcile while disabled.
            await RunOrphanResweepOnceAsync(stoppingToken);
            return;
        }

        _logger.LogInformation("Chat retention is enabled; conversations older than {RetentionDays} day(s) are auto-deleted every {SweepInterval}.",
            _options.RetentionDays,
            _options.SweepInterval);

        // Reconcile any stranded upload directory at startup, before the first timer tick (each subsequent full sweep
        // also runs the orphan resweep).
        await RunOrphanResweepOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RunSweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Retention sweep failed.");
            }
        }
    }

    // Runs one sweep pass: delete expired conversations' DB footprint + upload blobs, then orphan-resweep. Internal so
    // a test can drive a single deterministic pass without the periodic timer.
    internal async Task RunSweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var retentionStore = scope.ServiceProvider.GetRequiredService<INodeRetentionStore>();
        var uploadedFileStore = scope.ServiceProvider.GetRequiredService<IConversationUploadedFileStore>();
        var workSessionStore = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var workSessionArtifactBlobStore = scope.ServiceProvider.GetRequiredService<IWorkSessionArtifactBlobStore>();

        // Production timestamps are Unix MILLISECONDS, so the cutoff must be too: a seconds cutoff is a thousandfold
        // smaller than any real last_seen and the age predicate would never fire.
        var cutoffUtc = _timeProvider.GetUtcNow().Subtract(TimeSpan.FromDays(_options.RetentionDays)).ToUnixTimeMilliseconds();

        // Candidates are selected lock-free, then deleted under the conversation's exclusive write lock with
        // eligibility re-checked inside the transaction, so a conversation touched after selection survives.
        var candidateConversationIds = await retentionStore.ListExpiredConversationCandidatesAsync(cutoffUtc, cancellationToken);

        var deletedConversations = new List<(Guid ConversationId, Guid? WorkSessionId)>(candidateConversationIds.Count);
        foreach (var conversationId in candidateConversationIds)
        {
            // The owned session (1:1, may be none) is resolved BEFORE the purge: its row carries the only
            // conversation → session mapping, and the purge deletes it along with everything else.
            var workSession = await workSessionStore.FindByConversationAsync(conversationId, cancellationToken);

            var deleted = await _writer.ExecuteConversationExclusiveAsync(conversationId,
                (dbContext, token) => ConversationRetentionPurge.TryPurgeIfExpiredAsync(dbContext, conversationId, cutoffUtc, token),
                cancellationToken);
            if (deleted)
            {
                deletedConversations.Add((conversationId, workSession?.Id));
            }
        }

        foreach (var (conversationId, workSessionId) in deletedConversations)
        {
            await uploadedFileStore.DeleteAllForConversationAsync(conversationId, cancellationToken);
            if (workSessionId is { } sessionId)
            {
                // Work-session artifact bytes live on disk under the session id, out of the row purge's reach. A crash
                // before this call strands the directory until the next start's PurgeOrphanedArtifactScopesAsync.
                workSessionArtifactBlobStore.DeleteSession(sessionId);
            }
        }

        var orphanCount = await PurgeOrphanedUploadDirectoriesAsync(scope, uploadedFileStore, cancellationToken);

        if (deletedConversations.Count > 0 || orphanCount > 0)
        {
            _logger.LogInformation("Retention sweep deleted {DeletedConversationCount} conversation(s) and {OrphanCount} orphaned upload director(ies).",
                deletedConversations.Count,
                orphanCount);
        }
    }

    // Runs the orphaned-upload resweep on its own scope, at startup in both enabled and disabled modes, and is
    // failure-tolerant so a resweep error never crashes the host. Internal so a test can drive it deterministically.
    internal async Task RunOrphanResweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var uploadedFileStore = scope.ServiceProvider.GetRequiredService<IConversationUploadedFileStore>();
            var orphanCount = await PurgeOrphanedUploadDirectoriesAsync(scope, uploadedFileStore, cancellationToken);
            if (orphanCount > 0)
            {
                _logger.LogInformation("Retention orphan resweep removed {OrphanCount} orphaned upload director(ies).", orphanCount);
            }

            await PurgeOrphanedArtifactScopesAsync(scope, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-resweep; nothing durable is left inconsistent — it retries on the next start.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Retention orphan resweep failed.");
        }
    }

    /// <summary>Reclaims the artifact bytes of a work session or a workflow run whose owning row is gone.</summary>
    /// <remarks>
    ///     The state a crash between the row commit and the best-effort <c>DeleteSession</c>/<c>DeleteRun</c> beside
    ///     it leaves for good. Scope-keyed, never blob-keyed: a writer commits the blob BEFORE its row, so a blob id
    ///     with no row may still be in flight, while the scope's row is committed before the scope directory can
    ///     exist. Development Mode shares the store and needs no sweep — nothing deletes a project, so no scope of
    ///     its own can be orphaned.
    /// </remarks>
    private async Task PurgeOrphanedArtifactScopesAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        var cutoffUtc = _timeProvider.GetUtcNow() - OrphanedScopeGrace;

        // Both modules are feature-gated and this resweep runs on every start, enabled or not, so an absent store is
        // an ordinary configuration rather than a failure: nothing wrote those bytes, so nothing has to reclaim them.
        if (scope.ServiceProvider.GetService<IWorkSessionArtifactBlobStore>() is { } sessionBlobs)
        {
            await PurgeOrphanedScopesAsync(scope,
                "SELECT id FROM agent_work_sessions WHERE id = {0}",
                sessionBlobs.ListSessionIdsLastWrittenBefore(cutoffUtc),
                sessionBlobs.DeleteSession,
                "work session",
                cancellationToken);
        }

        if (scope.ServiceProvider.GetService<IDevWorkflowArtifactBlobStore>() is { } runBlobs)
        {
            await PurgeOrphanedScopesAsync(scope,
                "SELECT id FROM dev_workflow_runs WHERE id = {0}",
                runBlobs.ListRunIdsLastWrittenBefore(cutoffUtc),
                runBlobs.DeleteRun,
                "development workflow run",
                cancellationToken);
        }
    }

    private async Task PurgeOrphanedScopesAsync(AsyncServiceScope scope,
        string ownerProbeSql,
        IReadOnlyList<Guid> scopeIds,
        Action<Guid> deleteScope,
        string subject,
        CancellationToken cancellationToken)
    {
        if (scopeIds.Count == 0)
        {
            return;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var orphanCount = 0;
        foreach (var scopeId in scopeIds)
        {
            try
            {
                // A per-id existence probe stays bounded, like the upload resweep above: the candidate set is the
                // scopes that actually hold bytes, not every row of the owning table.
                var ownerExists = await dbContext.Database.SqlQueryRaw<Guid>(ownerProbeSql, scopeId).AnyAsync(cancellationToken);
                if (ownerExists)
                {
                    continue;
                }

                deleteScope(scopeId);
                orphanCount++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One unreadable directory or failed probe must not cost the remaining candidates their sweep.
                _logger.LogWarning(exception, "Could not reclaim the orphaned {Subject} artifact directory {ScopeId}.", subject, scopeId);
            }
        }

        if (orphanCount > 0)
        {
            _logger.LogInformation("Retention orphan resweep reclaimed {OrphanCount} orphaned {Subject} artifact director(ies).", orphanCount, subject);
        }
    }

    private static async Task<int> PurgeOrphanedUploadDirectoriesAsync(AsyncServiceScope scope,
        IConversationUploadedFileStore uploadedFileStore,
        CancellationToken cancellationToken)
    {
        var directoryIds = uploadedFileStore.ListConversationDirectoryIds();
        if (directoryIds.Count == 0)
        {
            return 0;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var orphanCount = 0;
        foreach (var directoryId in directoryIds)
        {
            // Upload directories only ever exist for conversations that had uploads, so this set is small; a per-id
            // existence probe stays bounded and avoids materializing every conversation id.
            var conversationExists = await dbContext.Database
                                                    .SqlQueryRaw<Guid>("SELECT conversation_id FROM conversations WHERE conversation_id = {0}", directoryId)
                                                    .AnyAsync(cancellationToken);
            if (conversationExists)
            {
                continue;
            }

            // No conversation row owns this directory — a leftover from a purge whose blob teardown did not complete.
            await uploadedFileStore.DeleteAllForConversationAsync(directoryId, cancellationToken);
            orphanCount++;
        }

        return orphanCount;
    }
}
