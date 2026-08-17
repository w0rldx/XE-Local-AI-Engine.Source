namespace XE_Local_AI_Engine.Client.Services.Models;

public sealed class LocalModelDeletionStartupReconciler(
    IServiceScopeFactory scopeFactory,
    ILogger<LocalModelDeletionStartupReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ILocalModelDeletionJournalReconciler>()
                       .ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogCritical(exception, "Installed-model deletion recovery failed; installed model mutations are unsafe.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
