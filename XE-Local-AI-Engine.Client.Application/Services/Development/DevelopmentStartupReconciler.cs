namespace XE_Local_AI_Engine.Client.Services.Development;

public sealed class DevelopmentStartupReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DevelopmentStartupReconciler(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        _ = await coordinator.ReconcileStartupAsync(cancellationToken);

        // A project created before the command-profile column carries no profile and cannot execute; one pass fills
        // every such project. The same service runs on project load, so an offline repository needs no restart.
        var backfill = scope.ServiceProvider.GetRequiredService<IDevelopmentProfileBackfillService>();
        _ = await backfill.BackfillAllAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
