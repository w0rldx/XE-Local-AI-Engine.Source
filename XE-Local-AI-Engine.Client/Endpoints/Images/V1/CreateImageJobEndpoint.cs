namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     FastEndpoints handler that enqueues a text-to-image job (POST images/jobs). Thin transport over the
///     <see cref="IImageJobCoordinator" />: it validates the prompt/model, hands a provider-neutral input to the
///     coordinator (which persists the job Queued with the prompt encrypted at rest and runs generation detached), then
///     returns the freshly-created Queued view. Operator-gated.
/// </summary>
public sealed class CreateImageJobEndpoint(IImageJobCoordinator coordinator)
    : Endpoint<CreateImageJobRequest, ImageJobResponse>
{
    private readonly IImageJobCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.Jobs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateImageJobRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            AddError("A prompt is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var jobId = await _coordinator.EnqueueAsync(req.ToInput(), ct).ConfigureAwait(false);

        var view = await _coordinator.GetAsync(jobId, ct).ConfigureAwait(false);
        if (view is null)
        {
            // The coordinator just persisted the job; a null view here means an unexpected read miss.
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(view.ToResponse(), ct).ConfigureAwait(false);
    }
}
