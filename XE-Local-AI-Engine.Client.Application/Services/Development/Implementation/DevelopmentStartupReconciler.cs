namespace XE_Local_AI_Engine.Client.Services.Development.Implementation;

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
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
