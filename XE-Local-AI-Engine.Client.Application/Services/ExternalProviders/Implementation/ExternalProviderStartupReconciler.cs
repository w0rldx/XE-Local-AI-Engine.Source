namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

/// <summary>
///     Runs one external-provider reconciliation pass at startup and primes the registry snapshot.
/// </summary>
/// <remarks>
///     <para>
///         Two jobs, one pass. The reconciliation repairs a node that crashed between the encrypted store's commit and
///         the provider-map/allow-list writes that follow it. The priming makes the registry's cached generation
///         available before the first chat turn, which is what the synchronous, fail-closed trust classification on the
///         send path answers from — without it, the first turn after a boot would withhold tools from a
///         perfectly-configured declared-local external model.
///     </para>
///     <para>
///         A failure here is logged, NOT rethrown, unlike the installed-model deletion recovery. External connections
///         are an optional feature: refusing to start the whole node because one encrypted file could not be read would
///         take chat, local models and every other surface down with it, while the fail-closed trust resolver already
///         makes the degraded state safe.
///     </para>
/// </remarks>
public sealed class ExternalProviderStartupReconciler(
    IServiceScopeFactory scopeFactory,
    IExternalProviderRegistryCache registryCache,
    ILogger<ExternalProviderStartupReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            _ = await scope.ServiceProvider.GetRequiredService<IExternalProviderReconciler>()
                           .ReconcileAsync(cancellationToken).ConfigureAwait(false);
            await registryCache.PrimeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "External provider reconciliation failed at startup; external models may not route until the next save. Trust classification stays fail-closed in the meantime.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
