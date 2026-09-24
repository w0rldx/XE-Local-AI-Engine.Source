namespace XE_Local_AI_Engine.Client.Services.Models;

public sealed class LocalModelDeletionStartupReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LocalModelDeletionStartupReconciler> _logger;

    public LocalModelDeletionStartupReconciler(IServiceScopeFactory scopeFactory,
        ILogger<LocalModelDeletionStartupReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ILocalModelDeletionJournalReconciler>()
                       .ReconcileAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogCritical(exception, "Installed-model deletion recovery failed; installed model mutations are unsafe.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
