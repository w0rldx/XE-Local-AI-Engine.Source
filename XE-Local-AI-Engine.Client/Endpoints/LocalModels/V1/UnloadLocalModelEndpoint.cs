namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     Gracefully evicts a model from the local runtimes' memory, wherever it is resident.
/// </summary>
/// <remarks>
///     The two-runtime fan-out — every llama-server role, then the gated Ollama eviction — belongs to
///     <see cref="IModelUnloadCoordinator" />; this endpoint binds, delegates and maps. Idempotent: unloading a model
///     that is not loaded still reports success. <c>Unloaded</c> is false only when a llama-server process was still
///     busy when the bounded drain window elapsed and was therefore left running. The model name is carried in the
///     route, so the client sends no body at all — see <see cref="Configure" /> for the Accepts override.
/// </remarks>
public sealed class UnloadLocalModelEndpoint : Endpoint<UnloadLocalModelRequest, UnloadLocalModelResponse>
{
    private readonly ModelNameValidator _modelNameValidator;
    private readonly IModelUnloadCoordinator _unloadCoordinator;

    public UnloadLocalModelEndpoint(
        IModelUnloadCoordinator unloadCoordinator,
        ModelNameValidator modelNameValidator)
    {
        ArgumentNullException.ThrowIfNull(modelNameValidator);
        ArgumentNullException.ThrowIfNull(unloadCoordinator);
        _modelNameValidator = modelNameValidator;
        _unloadCoordinator = unloadCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalModels.Unload);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the model name binds from the route, so a well-behaved client sends no body and no Content-Type, which the default POST "Accepts" metadata answers
        // with 415. Overriding Accepts lets it through. Sending a dummy body instead is NOT an option: the generated client's requestValidator types this body as `never`.
        Description(x => x.Accepts<UnloadLocalModelRequest>());
    }

    public override async Task HandleAsync(UnloadLocalModelRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and unload
        // the decoded canonical name to keep "validated name == unloaded name" true.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        if (!await ValidateModelNameAsync(decodedModelName, ct))
        {
            return;
        }

        var modelName = decodedModelName!.Trim();
        var unloaded = await _unloadCoordinator.UnloadAsync(modelName, ct);

        await Send.OkAsync(new UnloadLocalModelResponse
        {
            ModelName = modelName,
            Unloaded = unloaded
        }, ct);
    }

    private async Task<bool> ValidateModelNameAsync(string? modelName, CancellationToken ct)
    {
        var validationError = _modelNameValidator.GetValidationError(modelName);
        if (validationError is null)
        {
            return true;
        }

        AddError(validationError);
        await Send.ErrorsAsync(cancellation: ct);
        return false;
    }
}
