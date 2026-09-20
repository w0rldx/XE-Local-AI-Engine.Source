namespace XE_Local_AI_Engine.Client.Hosting;

using XE_Local_AI_Engine.Client.Services.Models;

/// <summary>
///     One-time startup backfill that closes the FRR-2 upgrade gap: every installed Ollama model pulled on an earlier
///     build carries no <c>model_provider_map</c> row, and is mapped to <c>ollama</c> here.
/// </summary>
/// <remarks>
///     Under the <c>llamacpp</c> unmapped-routing default those models would silently re-route to llama.cpp and fail to
///     dial Ollama on the next send. Already-mapped models are left untouched, so this is purely a migration for
///     pre-existing data. It runs in <see cref="ExecuteAsync" />, off the startup path, so a slow or unreachable Ollama
///     never blocks the host coming up; it is offline-tolerant and idempotent, and not desktop-gated. See
///     docs/wiki/11-hosting-and-deployment.md ("Upgrade backfills and their discriminators").
/// </remarks>
public sealed class OllamaProviderMapBackfillService : BackgroundService
{
    private readonly ILogger<OllamaProviderMapBackfillService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public OllamaProviderMapBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<OllamaProviderMapBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await BackfillAsync(_scopeFactory, _logger, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — nothing to backfill.
        }
    }

    /// <summary>
    ///     Maps every installed Ollama model that has no <c>model_provider_map</c> row to the Ollama provider.
    /// </summary>
    /// <remarks>
    ///     Best-effort: a failure to list (Ollama absent or unreachable) or to upsert a single row is logged and
    ///     skipped rather than surfaced, so the backfill never blocks startup. Exposed as <see langword="internal" />
    ///     so it is unit-testable without standing up the hosted-service lifecycle.
    /// </remarks>
    internal static async Task BackfillAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = scopeFactory.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IOllamaProviderMapBackfillCoordinator>();
        try
        {
            var mapped = await coordinator.BackfillAsync(cancellationToken);
            if (mapped > 0)
            {
                logger.LogInformation("Backfilled {Count} pre-existing Ollama model(s) to the ollama provider map.", mapped);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            // Ollama is optional and may be absent/unreachable: there is simply nothing to backfill. Pre-existing rows
            // (if any) are untouched; a later boot with Ollama running will complete the backfill.
            logger.LogDebug(exception, "Skipping Ollama provider-map backfill: the installed-model list could not be read.");
        }
    }
}
