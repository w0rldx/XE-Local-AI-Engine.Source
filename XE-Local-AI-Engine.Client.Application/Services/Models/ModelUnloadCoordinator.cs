namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <inheritdoc />
internal sealed class ModelUnloadCoordinator(
    IOllamaModelService modelService,
    ILlamaServerProcessSupervisor supervisor,
    IConfiguration configuration,
    ILogger<ModelUnloadCoordinator> logger) : IModelUnloadCoordinator
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ILogger<ModelUnloadCoordinator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    public async Task<bool> UnloadAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var unloaded = await EjectEveryRoleAsync(modelName, cancellationToken).ConfigureAwait(false);

        // The SAME gate AddOllamaRuntime uses to decide whether to register the Ollama provider (enabled unless
        // explicitly false). A node with the runtime switched off has no daemon to ask.
        if (_configuration.GetValue(OllamaRuntimeGate.RuntimeEnabledConfigurationKey, defaultValue: true))
        {
            await UnloadFromOllamaAsync(modelName, cancellationToken).ConfigureAwait(false);
        }

        return unloaded;
    }

    /// <summary>
    ///     Gracefully ejects every role-specific llama-server process for the model. The role set comes from
    ///     <see cref="Enum.GetValues{TEnum}" /> so a future <see cref="ModelRole" /> member is ejected too instead of
    ///     silently staying resident. A role that is not running reports <c>NotRunning</c>, which is success — the whole
    ///     operation is idempotent. Only a process still busy after the drain window (left running on purpose, because
    ///     force is not set here) makes the unload unsuccessful.
    /// </summary>
    private async Task<bool> EjectEveryRoleAsync(string modelName, CancellationToken cancellationToken)
    {
        var unloaded = true;
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var outcome = await _supervisor.EjectAsync(modelName, role, force: false, cancellationToken).ConfigureAwait(false);
            unloaded &= outcome is not LlamaServerEjectOutcome.TimedOutStillBusy;
        }

        return unloaded;
    }

    private async Task UnloadFromOllamaAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            await _modelService.UnloadModelAsync(modelName, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            // Desktop mode runs no Ollama daemon, so the connection is refused — a transport failure, which carries NO
            // status code. Nothing of this model is resident there, which is the outcome the caller asked for. Debug,
            // mirroring GetRunningLocalModelsEndpoint, because the operator ejects from a page that polls that endpoint
            // against the same absent daemon. A daemon that ANSWERS with a failure status is deliberately not caught
            // here: the 404 for an unknown model is already absorbed as idempotent inside OllamaModelUnloader, so
            // anything still arriving with a status (a 5xx) is a real fault the operator must see.
            _logger.LogDebug(exception, "Ollama not reachable while unloading a model; nothing was resident there.");
        }
    }
}
