namespace XE_Local_AI_Engine.Client.BackgroundServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Ages out whole conversations once they pass the configured retention window, deleting the complete footprint the
///     interactive immediate-purge deletes: every child DB row (via <see cref="INodeRetentionStore" />) <b>and</b> the
///     on-disk upload blobs. Retention permanently destroys user chat history, so it is <b>disabled by default</b> —
///     see <see cref="ChatRetentionOptions" />. The DB rows are deleted and committed first; the on-disk blobs are torn
///     down after the commit, and an orphan resweep on each pass removes any upload directory whose conversation row no
///     longer exists (covering a crash between the commit and the blob teardown), so an interruption can never leave a
///     permanent orphan.
/// </summary>
public sealed class RetentionSweeperService : BackgroundService
{
    private readonly ILogger<RetentionSweeperService> _logger;
    private readonly ChatRetentionOptions _options;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public RetentionSweeperService(IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        IOptions<ChatRetentionOptions> options,
        ILogger<RetentionSweeperService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Chat retention is disabled; conversations are never auto-deleted. Set {Section}:Enabled=true to enable it.", ChatRetentionOptions.Section);

            // Inactivity-based conversation deletion stays gated on Enabled, but the orphaned-upload resweep must run
            // regardless: a failed interactive purge can strand an upload directory whose conversation row is already
            // gone, and with retention disabled (the default) nothing else would ever reconcile it.
            await RunOrphanResweepOnceAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Chat retention is enabled; conversations older than {RetentionDays} day(s) are auto-deleted every {SweepInterval}.",
            _options.RetentionDays,
            _options.SweepInterval);

        // Reconcile any stranded upload directory at startup, before the first timer tick (each subsequent full sweep
        // also runs the orphan resweep).
        await RunOrphanResweepOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
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
                await RunSweepOnceAsync(stoppingToken).ConfigureAwait(false);
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
        var cutoffUtc = _timeProvider.GetUtcNow().Subtract(TimeSpan.FromDays(_options.RetentionDays)).ToUnixTimeSeconds();

        // DB rows first (committed inside the store), then the on-disk upload blobs for exactly the deleted ids.
        var deletedConversationIds = await retentionStore.SweepExpiredConversationsAsync(cutoffUtc, cancellationToken).ConfigureAwait(false);
        foreach (var conversationId in deletedConversationIds)
        {
            await uploadedFileStore.DeleteAllForConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }

        var orphanCount = await PurgeOrphanedUploadDirectoriesAsync(scope, uploadedFileStore, cancellationToken).ConfigureAwait(false);

        if (deletedConversationIds.Count > 0 || orphanCount > 0)
        {
            _logger.LogInformation("Retention sweep deleted {DeletedConversationCount} conversation(s) and {OrphanCount} orphaned upload director(ies).",
                deletedConversationIds.Count,
                orphanCount);
        }
    }

    // Runs the orphaned-upload resweep on its own scope, independent of the inactivity-based conversation deletion.
    // Used at startup in both enabled and disabled modes; failure-tolerant so a resweep error never crashes the host.
    // Internal so a test can drive it deterministically with retention disabled.
    internal async Task RunOrphanResweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var uploadedFileStore = scope.ServiceProvider.GetRequiredService<IConversationUploadedFileStore>();
            var orphanCount = await PurgeOrphanedUploadDirectoriesAsync(scope, uploadedFileStore, cancellationToken).ConfigureAwait(false);
            if (orphanCount > 0)
            {
                _logger.LogInformation("Retention orphan resweep removed {OrphanCount} orphaned upload director(ies).", orphanCount);
            }
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
                                                    .AnyAsync(cancellationToken)
                                                    .ConfigureAwait(false);
            if (conversationExists)
            {
                continue;
            }

            // No conversation row owns this directory — a leftover from a purge whose blob teardown did not complete.
            await uploadedFileStore.DeleteAllForConversationAsync(directoryId, cancellationToken).ConfigureAwait(false);
            orphanCount++;
        }

        return orphanCount;
    }
}
