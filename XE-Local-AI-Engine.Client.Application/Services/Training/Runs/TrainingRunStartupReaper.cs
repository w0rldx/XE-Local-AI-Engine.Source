namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Startup sweep for what a previous process left behind: a trainer that outlived its host, and the decrypted
///     scratch its run never got to delete.
/// </summary>
/// <remarks>
///     Identity checks and the kill live in <see cref="TrainingProcessReaper" />. The scratch sweep is age-gated like
///     <c>GgufAcquisitionArtifactStartupReaper</c>: a <c>work/</c> directory past the stale window holds decrypted data.
/// </remarks>
public sealed class TrainingRunStartupReaper : IHostedService
{
    internal static readonly TimeSpan StaleWorkAge = TimeSpan.FromHours(6);

    private readonly ITrainingProcessInspector _inspector;
    private readonly ILogger<TrainingRunStartupReaper> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TrainingRunWorkspace _workspace;

    public TrainingRunStartupReaper(IServiceScopeFactory scopeFactory,
        ITrainingProcessInspector inspector,
        TrainingRunWorkspace workspace,
        TimeProvider timeProvider,
        ILogger<TrainingRunStartupReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(workspace);
        _inspector = inspector;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _workspace = workspace;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReapAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A sweep failure must never block node startup.
            _logger.LogError(exception, "The training run startup sweep failed.");
        }

        SweepStaleWork();
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task ReapAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();

        // Every receipt, unpaged: a live trainer whose run sits behind a page of newer runs is exactly the one that
        // must not be missed. Recovery does not touch the column, so the read order against it does not matter.
        foreach (var entry in await store.ListLaunchReceiptsAsync(cancellationToken))
        {
            await TrainingProcessReaper.ReapOneAsync(_inspector,
                entry.RunId,
                entry.LaunchReceiptJson,
                ct => store.SetLaunchReceiptAsync(entry.RunId, launchReceiptJson: null, ct),
                _logger,
                cancellationToken);
        }

        var failed = await store.RecoverOnStartupAsync(cancellationToken);
        if (failed.Count > 0)
        {
            _logger.LogWarning("Marked {RunCount} training runs interrupted by the previous shutdown as failed; interrupted runs are never resumed.",
                failed.Count);
        }
    }

    private void SweepStaleWork()
    {
        var root = _workspace.RunsRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var runDirectory in Directory.EnumerateDirectories(root))
        {
            var work = Path.Combine(runDirectory, "work");
            try
            {
                var info = new DirectoryInfo(work);
                if (info.Exists && now - info.LastWriteTimeUtc >= StaleWorkAge)
                {
                    info.Delete(recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "A stale training work directory could not be removed; the next sweep retries it.");
            }
        }
    }
}
