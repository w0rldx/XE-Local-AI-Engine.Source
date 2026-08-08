namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

/// <summary>
///     Runs <see cref="IToolCapableModelRegistrar.BackfillInstalledAsync" /> once at startup, off the critical path.
/// </summary>
/// <remarks>
///     <para>
///         Without this, feeding capability in at download time would only ever fix models downloaded AFTER the change —
///         every model already on the node would stay silently tool-less, which is precisely the reported state (a user
///         had followed the app's own recommendation and downloaded a tool-capable model that the allow-list did not
///         contain).
///     </para>
///     <para>
///         Best-effort by design: the node must start even if the model registry or the settings file cannot be read, so
///         a failure is logged and swallowed rather than taking the host down. The work is additive and idempotent, so a
///         missed run simply corrects itself on the next start or the next download.
///     </para>
/// </remarks>
internal sealed class ToolCapableModelBackfillService : BackgroundService
{
    private readonly ILogger<ToolCapableModelBackfillService> _logger;
    private readonly IToolCapableModelRegistrar _registrar;

    public ToolCapableModelBackfillService(IToolCapableModelRegistrar registrar, ILogger<ToolCapableModelBackfillService> logger)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first so host startup is never blocked on a model-registry read.
        await Task.Yield();

        try
        {
            _ = await _registrar.BackfillInstalledAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down during startup — nothing to report.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Could not backfill the tool-capable model list from the installed GGUF descriptors; the configured list still applies.");
        }
    }
}
