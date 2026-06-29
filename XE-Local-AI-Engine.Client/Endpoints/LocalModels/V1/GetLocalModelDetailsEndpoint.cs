namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

public sealed class GetLocalModelDetailsEndpoint(
    IOllamaModelService modelService,
    ILocalModelProviderResolver providerResolver,
    IGgufModelStore ggufModelStore,
    ICloudCredentialStore cloudCredentialStore,
    ModelNameValidator modelNameValidator) : Endpoint<GetLocalModelDetailsRequest, LocalModelDetailsResponse>
{
    private readonly ICloudCredentialStore _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));
    private readonly IGgufModelStore _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.ModelDetails);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetLocalModelDetailsRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and probe
        // the decoded canonical name to keep "validated name == probed name" true.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        if (!await ValidateModelNameAsync(decodedModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var modelName = decodedModelName!.Trim();

        // A Codex cloud model id (e.g. gpt-5.5) is NOT a local Ollama model: probing the local runtime's /api/show
        // for it 500s (Ollama has no such model). Model details (context window, template, license) are a
        // local-runtime concept, so a cloud id has no local details — return a clean 404 instead of a 500. The chat
        // UI should not request local details for a cloud model at all.
        if (CodexModelCatalog.IsCodexModel(modelName))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        // An Azure Foundry deployment id is likewise NOT a local Ollama model: model details (context window, template,
        // license) are local-runtime concepts an Azure deployment has no equivalent of, and probing /api/show for it
        // would 500. Return a clean 404 instead, matching the Codex branch above.
        if (await IsAzureFoundryModelAsync(modelName, ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        // Route by the model's provider BEFORE probing: a GGUF (llama.cpp) model has no Ollama /api/show entry, so its
        // details come from the GGUF store, not the Ollama daemon. This also means a GGUF selection never touches Ollama
        // — in desktop mode (no Ollama daemon) that avoids both the connect stall and the 404 the old Ollama-only path
        // returned. Provider resolution runs on the DECODED name so "validated/resolved name == probed name".
        var providerName = await _providerResolver.ResolveProviderNameForModelAsync(modelName, ct).ConfigureAwait(false);
        if (string.Equals(providerName, LocalModelProviders.LlamaCpp, StringComparison.OrdinalIgnoreCase))
        {
            await SendGgufModelDetailsAsync(modelName, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var details = await _modelService.ShowModelDetailsAsync(modelName, ct).ConfigureAwait(false);
            await Send.OkAsync(details.ToResponse(modelName), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // Details come from the Ollama daemon's /api/show. In desktop mode the Ollama endpoint isn't running at all,
            // so the probe throws a connection error. That is an absence of local details, not a server fault: degrade
            // to a clean 404 instead of bubbling a 500. Debug, not Warning: the chat UI polls this per selected model —
            // logging a Warning + stack trace here would flood the console. The 404 is the intended graceful degradation.
            Logger.LogDebug(exception, "Model details unavailable for '{ModelName}': the local Ollama runtime is unreachable.", modelName);
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }

    // Builds the details response for a llama.cpp-served GGUF from the installed-model registry — no Ollama probe.
    // Maps onto the SAME LocalModelDetailsResponse shape the Ollama branch returns: only MaxContextTokens is a GGUF
    // concept (read from the descriptor). Template/System/License are Ollama Modelfile concepts a GGUF has no
    // equivalent of, so they stay null. A model that resolves to llamacpp but isn't in the installed registry (a stale
    // map row, or one removed on disk) has no details — a clean 404, matching the Ollama "no entry" semantics.
    private async Task SendGgufModelDetailsAsync(string modelName, CancellationToken ct)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(ct).ConfigureAwait(false);
        var descriptor = installed.FirstOrDefault(model => string.Equals(model.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(descriptor.ToDetailsResponse(modelName), ct).ConfigureAwait(false);
    }

    // True when the model id matches one of the stored Azure Foundry connection's deployment names (ordinal,
    // case-insensitive). Best-effort: any failure resolving the encrypted config is treated as "not Azure" so the
    // existing local-routing path still runs.
    private async Task<bool> IsAzureFoundryModelAsync(string modelName, CancellationToken ct)
    {
        try
        {
            var config = await _cloudCredentialStore.LoadConfigAsync(ct).ConfigureAwait(false);
            var connection = config?.AzureFoundry;
            return connection is { Models.Count: > 0 }
                   && connection.Models.Any(model => string.Equals(model.DeploymentName, modelName, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "Azure Foundry deployment match could not be resolved for '{ModelName}'.", modelName);
            return false;
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
