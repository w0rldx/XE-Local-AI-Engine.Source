namespace XE_Local_AI_Engine.Client.BackgroundServices;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Application service for retention sweeper behavior.
/// </summary>
public sealed class RetentionSweeperService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    private readonly ILogger<RetentionSweeperService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public RetentionSweeperService(IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        ILogger<RetentionSweeperService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _timeProvider);

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
                await SweepAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var retentionStore = scope.ServiceProvider.GetRequiredService<INodeRetentionStore>();
        var cutoffUtc = _timeProvider.GetUtcNow().Subtract(RetentionWindow).ToUnixTimeSeconds();

        var deletedConversationCount = await retentionStore.SweepExpiredConversationsAsync(cutoffUtc, cancellationToken)
                                                           .ConfigureAwait(false);

        if (deletedConversationCount == 0)
        {
            return;
        }

        _logger.LogInformation("Retention sweep deleted {DeletedConversationCount} conversations.", deletedConversationCount);
    }
}
