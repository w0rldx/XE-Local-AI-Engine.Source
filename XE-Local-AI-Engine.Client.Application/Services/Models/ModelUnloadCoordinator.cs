namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <inheritdoc />
internal sealed class ModelUnloadCoordinator : IModelUnloadCoordinator
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelUnloadCoordinator> _logger;
    private readonly IOllamaModelService _modelService;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    public ModelUnloadCoordinator(
        IOllamaModelService modelService,
        ILlamaServerProcessSupervisor supervisor,
        IConfiguration configuration,
        ILogger<ModelUnloadCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(modelService);
        ArgumentNullException.ThrowIfNull(supervisor);
        _configuration = configuration;
        _logger = logger;
        _modelService = modelService;
        _supervisor = supervisor;
    }

    public async Task<bool> UnloadAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var unloaded = await EjectEveryRoleAsync(modelName, cancellationToken);

        // The SAME gate AddOllamaRuntime uses to decide whether to register the Ollama provider (enabled unless
        // explicitly false). A node with the runtime switched off has no daemon to ask.
        if (_configuration.GetValue(OllamaRuntimeGate.RuntimeEnabledConfigurationKey, defaultValue: true))
        {
            await UnloadFromOllamaAsync(modelName, cancellationToken);
        }

        return unloaded;
    }

    /// <summary>
    ///     Gracefully ejects every role-specific llama-server process for the model. The role set comes from
    ///     <see cref="Enum.GetValues{TEnum}" /> so a future <see cref="ModelRole" /> member is ejected too instead of silently staying
    ///     resident.
    /// </summary>
    /// <remarks>
    ///     A role that is not running reports <c>NotRunning</c>, which is success — the whole operation is idempotent. Only a process still
    ///     busy after the drain window (left running on purpose, because force is not set here) makes the unload unsuccessful.
    /// </remarks>
    private async Task<bool> EjectEveryRoleAsync(string modelName, CancellationToken cancellationToken)
    {
        var unloaded = true;
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var outcome = await _supervisor.EjectAsync(modelName, role, force: false, cancellationToken);
            unloaded &= outcome is not LlamaServerEjectOutcome.TimedOutStillBusy;
        }

        return unloaded;
    }

    /// <summary>Asks the Ollama daemon to evict the model, absorbing only an unreachable daemon.</summary>
    /// <remarks>
    ///     A refused connection (desktop mode runs no Ollama daemon) is a transport failure carrying NO status code, and means nothing of this
    ///     model is resident there — the outcome the caller asked for. It logs at Debug, mirroring
    ///     <c>GetRunningLocalModelsEndpoint</c>, because the operator ejects from a page polling that endpoint against the same absent daemon.
    ///     A daemon that ANSWERS with a failure status is deliberately not caught: <c>OllamaModelUnloader</c> already absorbs the
    ///     unknown-model 404 as idempotent, so anything still arriving with a status (a 5xx) is a real fault the operator must see.
    /// </remarks>
    private async Task UnloadFromOllamaAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            await _modelService.UnloadModelAsync(modelName, cancellationToken);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            _logger.LogDebug(exception, "Ollama not reachable while unloading a model; nothing was resident there.");
        }
    }
}
