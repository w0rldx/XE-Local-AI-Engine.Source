namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using System.Net.Http;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.CodexOAuth;

public sealed class GetLocalModelDetailsEndpoint(
    IOllamaModelService modelService,
    ModelNameValidator modelNameValidator) : Endpoint<GetLocalModelDetailsRequest, LocalModelDetailsResponse>
{
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));

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

        try
        {
            var details = await _modelService.ShowModelDetailsAsync(modelName, ct).ConfigureAwait(false);
            await Send.OkAsync(details.ToResponse(modelName), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // Details come from the Ollama daemon's /api/show. A GGUF served by llama.cpp has no Ollama entry, and in
            // desktop mode the Ollama endpoint isn't running at all — so the probe throws a connection error. That is
            // an absence of local details, not a server fault: degrade to a clean 404 instead of bubbling a 500.
            // Debug, not Warning: a GGUF model (and desktop mode generally) has no Ollama /api/show entry, and the chat
            // UI polls this per selected model — logging a Warning + stack trace here would flood the console. The 404
            // is the intended graceful degradation.
            Logger.LogDebug(exception, "Model details unavailable for '{ModelName}': the local Ollama runtime is unreachable.", modelName);
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
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
