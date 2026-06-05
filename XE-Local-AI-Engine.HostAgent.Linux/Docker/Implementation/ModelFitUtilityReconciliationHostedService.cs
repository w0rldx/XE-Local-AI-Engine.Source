namespace XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;

using global::Docker.DotNet;

/// <summary>
///     Startup reconciliation for the model-fit utility runner: removes any leftover utility containers
///     (those stamped with the <c>xe.modelfit.utility</c> label) that a prior crash orphaned, so an interrupted llmfit
///     run does not keep consuming CPU/GPU after the node restarts. Best-effort — a reconciliation failure is logged and
///     swallowed so the HostAgent still starts; the next start retries.
/// </summary>
public sealed class ModelFitUtilityReconciliationHostedService : IHostedService
{
    private readonly IDockerRuntimeClient _runtimeClient;
    private readonly ILogger<ModelFitUtilityReconciliationHostedService> _logger;

    public ModelFitUtilityReconciliationHostedService(
        IDockerRuntimeClient runtimeClient,
        ILogger<ModelFitUtilityReconciliationHostedService> logger)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await _runtimeClient.RemoveOrphanedUtilityContainersAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("Reconciled {Count} orphaned model-fit utility container(s) on startup.", removed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to reconcile.
        }
        catch (Exception exception) when (exception is DockerApiException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "Model-fit utility startup reconciliation failed; orphaned containers (if any) will be retried next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
