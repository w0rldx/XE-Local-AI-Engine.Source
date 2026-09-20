namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

/// <summary>
///     Runs one external-provider reconciliation pass at startup and primes the registry snapshot.
/// </summary>
/// <remarks>
///     Two jobs, one pass: the reconciliation repairs a node that crashed between the encrypted store's commit and the
///     provider-map/allow-list writes, and the priming makes the registry's cached generation available before the
///     first chat turn, which is what the synchronous fail-closed trust classification answers from. A failure here is
///     logged, NOT rethrown, unlike the installed-model deletion recovery — external connections are optional, and the
///     fail-closed trust resolver already makes the degraded state safe.
/// </remarks>
public sealed class ExternalProviderStartupReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExternalProviderRegistryCache _registryCache;
    private readonly ILogger<ExternalProviderStartupReconciler> _logger;

    public ExternalProviderStartupReconciler(
        IServiceScopeFactory scopeFactory,
        IExternalProviderRegistryCache registryCache,
        ILogger<ExternalProviderStartupReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _registryCache = registryCache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            _ = await scope.ServiceProvider.GetRequiredService<IExternalProviderReconciler>()
                           .ReconcileAsync(cancellationToken);
            await _registryCache.PrimeAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception,
                "External provider reconciliation failed at startup; external models may not route until the next save. Trust classification stays fail-closed in the meantime.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
