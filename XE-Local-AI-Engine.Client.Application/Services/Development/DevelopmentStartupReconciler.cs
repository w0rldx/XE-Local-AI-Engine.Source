namespace XE_Local_AI_Engine.Client.Services.Development;

public sealed class DevelopmentStartupReconciler(IServiceScopeFactory scopeFactory) : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        _ = await coordinator.ReconcileStartupAsync(cancellationToken).ConfigureAwait(false);

        // Projects created before the command-profile column existed carry no profile and cannot execute. Filling them
        // here covers every such project in one pass; the same service also runs on project load, so a repository that
        // happened to be offline at boot does not need a restart to become usable.
        var backfill = scope.ServiceProvider.GetRequiredService<IDevelopmentProfileBackfillService>();
        _ = await backfill.BackfillAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
