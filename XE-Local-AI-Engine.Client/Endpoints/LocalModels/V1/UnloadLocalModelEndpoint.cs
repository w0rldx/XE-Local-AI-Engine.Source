namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     Gracefully evicts a model from the local runtimes' memory, wherever it is resident. The two-runtime fan-out —
///     every llama-server role, then the gated Ollama eviction — belongs to <see cref="IModelUnloadCoordinator" />;
///     this endpoint binds, delegates and maps. Idempotent: unloading a model that is not loaded still reports success.
///     <c>Unloaded</c> is false only when a llama-server process was still busy when the bounded drain window elapsed
///     and was therefore left running. The model name is carried in the route, so the client sends no body at all — see
///     <see cref="Configure" /> for the Accepts override that keeps a body-less POST out of 415.
/// </summary>
public sealed class UnloadLocalModelEndpoint(
    IModelUnloadCoordinator unloadCoordinator,
    ModelNameValidator modelNameValidator) : Endpoint<UnloadLocalModelRequest, UnloadLocalModelResponse>
{
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly IModelUnloadCoordinator _unloadCoordinator = unloadCoordinator ?? throw new ArgumentNullException(nameof(unloadCoordinator));

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
        var unloaded = await _unloadCoordinator.UnloadAsync(modelName, ct).ConfigureAwait(false);

        await Send.OkAsync(new UnloadLocalModelResponse
        {
            ModelName = modelName,
            Unloaded = unloaded
        }, ct).ConfigureAwait(false);
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
