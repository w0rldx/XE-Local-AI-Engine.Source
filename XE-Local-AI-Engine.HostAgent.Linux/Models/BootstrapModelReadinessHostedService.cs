namespace XE_Local_AI_Engine.HostAgent.Linux.Models;

public sealed class BootstrapModelReadinessHostedService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly ILogger<BootstrapModelReadinessHostedService> _logger;

    private readonly BootstrapModelReadinessService _readinessService;

    public BootstrapModelReadinessHostedService(BootstrapModelReadinessService readinessService,
        ILogger<BootstrapModelReadinessHostedService> logger)
    {
        _readinessService = readinessService ?? throw new ArgumentNullException(nameof(readinessService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = await _readinessService.EnsureReadyAsync(stoppingToken).ConfigureAwait(false);
            if (snapshot.IsReady)
            {
                _logger.LogInformation("Bootstrap model {BootstrapModel} is ready.", snapshot.ModelName);
                return;
            }

            _logger.LogInformation("Bootstrap model {BootstrapModel} is not ready yet: {Diagnostics}.",
                snapshot.ModelName,
                string.Join(", ", snapshot.Diagnostics));

            await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
        }
    }
}
