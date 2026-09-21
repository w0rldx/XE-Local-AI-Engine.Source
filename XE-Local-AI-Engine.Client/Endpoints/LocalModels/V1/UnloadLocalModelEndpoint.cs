namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;

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
    private readonly IModelUnloadCoordinator _unloadCoordinator;

    public UnloadLocalModelEndpoint(
        IModelUnloadCoordinator unloadCoordinator)
    {
        ArgumentNullException.ThrowIfNull(unloadCoordinator);
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
        // Decode again here: UnloadLocalModelRequestValidator already ran the grammar over the decoded name, and
        // unloading the same decoded name keeps "validated name == unloaded name" true. See ModelRouteName.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);

        var modelName = decodedModelName!.Trim();
        var unloaded = await _unloadCoordinator.UnloadAsync(modelName, ct);

        await Send.OkAsync(new UnloadLocalModelResponse
        {
            ModelName = modelName,
            Unloaded = unloaded
        }, ct);
    }
}
