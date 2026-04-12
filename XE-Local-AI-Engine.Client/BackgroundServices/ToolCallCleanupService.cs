namespace XE_Local_AI_Engine.Client.BackgroundServices;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Invocation;

public sealed class ToolCallCleanupService : BackgroundService
{
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<ToolCallCleanupService> _logger;
    private readonly IOptions<WorkerNodeOptions> _workerNodeOptions;

    public ToolCallCleanupService(IInvocationRunner invocationRunner,
        IOptions<WorkerNodeOptions> workerNodeOptions,
        ILogger<ToolCallCleanupService> logger)
    {
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _workerNodeOptions = workerNodeOptions ?? throw new ArgumentNullException(nameof(workerNodeOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleanupInterval = TimeSpan.FromSeconds(_workerNodeOptions.Value.CleanupIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(cleanupInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var maxAge = TimeSpan.FromMinutes(_workerNodeOptions.Value.MaxPendingToolCallAgeMinutes);
                _invocationRunner.CleanupStaleToolCalls(maxAge);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to clean up stale tool call state.");
            }
        }
    }
}
