namespace XE_Local_AI_Engine.Client.Hosting;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Ollama;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     One-time startup backfill that closes the FRR-2 upgrade gap: before the unmapped-routing default was flipped to
///     <c>llamacpp</c>, the Ollama pull endpoints never wrote a <c>model_provider_map</c> row, so every model pulled on an
///     EARLIER build is unmapped. Under the flipped default those models would silently re-route to llama.cpp and fail to
///     dial Ollama on the next send. This service maps each currently-installed Ollama model that lacks a map row to
///     <c>ollama</c>, restoring its routing. New pulls already write the row at pull time, so this is purely a migration
///     for pre-existing data — it leaves already-mapped models untouched.
/// </summary>
/// <remarks>
///     <para>
///         <b>Not desktop-gated.</b> A pre-existing Ollama install can exist on any launch mode, so the backfill runs on
///         every boot. It is a node-local read of installed models followed by additive upserts — it never starts an
///         advisor run, downloads anything, or contacts the central platform.
///     </para>
///     <para>
///         <b>Non-blocking + offline-tolerant + idempotent.</b> It runs in <see cref="ExecuteAsync" /> off the startup
///         path (mirroring <c>NodeChatTitleEncryptionBackfillService</c> and <c>FirstRunModelProvisioningService</c>), so
///         a slow/unreachable Ollama never blocks the host from coming up. Listing failures (Ollama absent/unreachable)
///         are swallowed and logged rather than surfaced, and each model is mapped only when it has no existing row, so
///         re-running on every boot is a cheap no-op once the rows exist. As an <see cref="IHostedService" /> it is also
///         removed by the test host's <c>RemoveAll&lt;IHostedService&gt;()</c>, so it never perturbs request-path tests.
///     </para>
/// </remarks>
public sealed class OllamaProviderMapBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<OllamaProviderMapBackfillService> logger) : BackgroundService
{
    private readonly ILogger<OllamaProviderMapBackfillService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await BackfillAsync(_scopeFactory, _logger, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — nothing to backfill.
        }
    }

    /// <summary>
    ///     Maps every installed Ollama model that has no <c>model_provider_map</c> row to the Ollama provider. Best-effort:
    ///     a failure to list (Ollama absent/unreachable) or to upsert a single row is logged and skipped rather than
    ///     surfaced, so the backfill never blocks startup. Exposed as <see langword="internal" /> so it is unit-testable
    ///     without standing up the hosted-service lifecycle.
    /// </summary>
    internal static async Task BackfillAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = scopeFactory.CreateAsyncScope();
        var ollamaModelService = scope.ServiceProvider.GetRequiredService<IOllamaModelService>();
        var mapStore = scope.ServiceProvider.GetRequiredService<IModelProviderMapStore>();

        IReadOnlyList<string> installedModelNames;
        try
        {
            var installed = await ollamaModelService.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
            installedModelNames = installed
                                  .Select(model => model.Name)
                                  .Where(name => !string.IsNullOrWhiteSpace(name))
                                  .ToArray();
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
            return;
        }

        if (installedModelNames.Count == 0)
        {
            return;
        }

        var mapped = 0;
        foreach (var modelName in installedModelNames)
        {
            try
            {
                var existing = await mapStore.GetProviderForModelAsync(modelName, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    // Already mapped (e.g. a forward pull, or a deliberate operator override): leave it untouched.
                    continue;
                }

                _ = await mapStore.UpsertAsync(modelName, OllamaLocalModelProvider.OllamaProviderName, cancellationToken).ConfigureAwait(false);
                mapped++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                // A single row failing to persist must not abort the whole backfill — the model keeps the (wrong) default
                // until the next boot, but the remaining models are still repaired.
                logger.LogWarning(exception, "Could not backfill the Ollama provider mapping for an installed model; skipping it.");
            }
        }

        if (mapped > 0)
        {
            logger.LogInformation("Backfilled {Count} pre-existing Ollama model(s) to the ollama provider map.", mapped);
        }
    }
}
