namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Gracefully evicts a model from the local runtimes' memory, wherever it is resident. BOTH runtimes are asked,
///     because the node cannot know which one holds the model: residency is a property of a running process, and the
///     per-model provider map only records where a model would be <em>served</em>. A model pulled into Ollama has no map
///     row at all, so routing this action by that map ejected nothing and reported success while Ollama still held the
///     weights.
///     <list type="bullet">
///         <item>
///             <description>
///                 First <see cref="ILlamaServerProcessSupervisor.EjectAsync" /> per <see cref="ModelRole" />, never
///                 forced. This costs no I/O when nothing is running, and stopping the child process is both what frees
///                 its VRAM and how edited launch arguments take effect: the next request respawns it.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Then the Ollama <c>keep_alive=0</c> eviction, when the optional Ollama runtime is enabled
///                 (<see cref="OllamaRuntimeGate.RuntimeEnabledConfigurationKey" />, the same gate
///                 <see cref="GetRunningLocalModelsEndpoint" /> reads). An unreachable daemon means nothing is resident
///                 there, which is not an error.
///             </description>
///         </item>
///     </list>
///     Both paths let an in-flight generation complete before the model is evicted, so unload never interrupts a running
///     turn. Idempotent: unloading a model that is not loaded still reports success. <c>Unloaded</c> is false only when a
///     llama-server process was still busy when the bounded drain window elapsed and was therefore left running. The
///     model name is carried in the route, so the client sends no body at all — see <see cref="Configure" /> for the
///     Accepts override that keeps a body-less POST out of 415.
/// </summary>
public sealed class UnloadLocalModelEndpoint(
    IOllamaModelService modelService,
    ILlamaServerProcessSupervisor supervisor,
    IConfiguration configuration,
    ModelNameValidator modelNameValidator,
    ILogger<UnloadLocalModelEndpoint> logger) : Endpoint<UnloadLocalModelRequest, UnloadLocalModelResponse>
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ILogger<UnloadLocalModelEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalModels.Unload);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the model name binds from the route, so a well-behaved client sends no body — and therefore
        // no Content-Type. The default POST "Accepts" metadata only allows application/json, which FastEndpoints answers
        // with 415 when the header is absent. Overriding Accepts lets the body-less eject request through. (Sending a
        // dummy "{}" instead is NOT an option: the generated client's requestValidator types this body as `never`.)
        Description(x => x.Accepts<UnloadLocalModelRequest>());
    }

    public override async Task HandleAsync(UnloadLocalModelRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and unload
        // the decoded canonical name to keep "validated name == unloaded name" true.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        if (!await ValidateModelNameAsync(decodedModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var modelName = decodedModelName!.Trim();
        var unloaded = await EjectEveryRoleAsync(modelName, ct).ConfigureAwait(false);

        // The SAME gate AddOllamaRuntime uses to decide whether to register the Ollama provider (enabled unless
        // explicitly false). A node with the runtime switched off has no daemon to ask.
        if (_configuration.GetValue(OllamaRuntimeGate.RuntimeEnabledConfigurationKey, defaultValue: true))
        {
            await UnloadFromOllamaAsync(modelName, ct).ConfigureAwait(false);
        }

        await Send.OkAsync(new UnloadLocalModelResponse
        {
            ModelName = modelName,
            Unloaded = unloaded
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Gracefully ejects every role-specific llama-server process for the model. The role set comes from
    ///     <see cref="Enum.GetValues{TEnum}" /> so a future <see cref="ModelRole" /> member is ejected too instead of
    ///     silently staying resident. A role that is not running reports <c>NotRunning</c>, which is success — the whole
    ///     endpoint is idempotent. Only a process still busy after the drain window (left running on purpose, because
    ///     force is not set here) makes the unload unsuccessful.
    /// </summary>
    private async Task<bool> EjectEveryRoleAsync(string modelName, CancellationToken ct)
    {
        var unloaded = true;
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var outcome = await _supervisor.EjectAsync(modelName, role, force: false, ct).ConfigureAwait(false);
            unloaded &= outcome is not LlamaServerEjectOutcome.TimedOutStillBusy;
        }

        return unloaded;
    }

    private async Task UnloadFromOllamaAsync(string modelName, CancellationToken ct)
    {
        try
        {
            await _modelService.UnloadModelAsync(modelName, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // Desktop mode runs no Ollama daemon, so the connection is refused. Nothing of this model is resident there,
            // which is the outcome the caller asked for — not a failure. Debug, mirroring GetRunningLocalModelsEndpoint,
            // because the operator ejects from a page that polls that endpoint against the same absent daemon.
            _logger.LogDebug(exception, "Ollama not reachable while unloading a model; nothing was resident there.");
        }
    }

    private async Task<bool> ValidateModelNameAsync(string? modelName, CancellationToken ct)
    {
        var validationError = _modelNameValidator.GetValidationError(modelName);
        if (validationError is null)
        {
            return true;
        }

        AddError(validationError);
        await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        return false;
    }
}
