namespace XE_Local_AI_Engine.Client.BackgroundServices;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Application service for tool call cleanup behavior.
/// </summary>
public sealed class ToolCallCleanupService : BackgroundService
{
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<ToolCallCleanupService> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly CancellationTokenSource _shutdownSignal = new();
    private readonly IOptions<WorkerNodeOptions> _workerNodeOptions;

    public ToolCallCleanupService(IInvocationRunner invocationRunner,
        IOptions<WorkerNodeOptions> workerNodeOptions,
        INodeRuntimeSettings runtimeSettings,
        ILogger<ToolCallCleanupService> logger)
    {
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _workerNodeOptions = workerNodeOptions ?? throw new ArgumentNullException(nameof(workerNodeOptions));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdownSignal.Token);
        var linkedToken = linkedCancellationTokenSource.Token;
        var cleanupInterval = TimeSpan.FromSeconds(_workerNodeOptions.Value.CleanupIntervalSeconds);

        while (!linkedToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(cleanupInterval, linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var maxPendingToolCallAgeMinutes = await _runtimeSettings.GetMaxPendingToolCallAgeMinutesAsync(linkedToken).ConfigureAwait(false);
                var maxAge = TimeSpan.FromMinutes(maxPendingToolCallAgeMinutes);
                _invocationRunner.CleanupStaleToolCalls(maxAge);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to clean up stale tool call state.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdownSignal.CancelAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _shutdownSignal.Dispose();
        base.Dispose();
    }
}
