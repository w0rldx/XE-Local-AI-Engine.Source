namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     FastEndpoints handler removing an installed image model's weights and registry entry
///     (DELETE images/models/{modelName}). Mirrors <c>DELETE models/{modelName}</c> on the GGUF lane.
/// </summary>
/// <remarks>
///     Idempotent: deleting a model that is not installed is a 204, matching the store's own contract. The route carries
///     the model name, which is operator-supplied text, so the store — not this endpoint — owns the containment guard
///     that keeps the delete inside the models directory.
/// </remarks>
public sealed class DeleteImageModelEndpoint(IImageModelStore modelStore)
    : Endpoint<DeleteImageModelRequest>
{
    private readonly IImageModelStore _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Images.ModelByName);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteImageModelRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await _modelStore.DeleteModelAsync(req.ModelName.Trim(), ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
