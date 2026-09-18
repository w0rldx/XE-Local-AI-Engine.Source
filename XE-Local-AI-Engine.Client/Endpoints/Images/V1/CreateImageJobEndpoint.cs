namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     FastEndpoints handler that enqueues a text-to-image job (POST images/jobs). Thin transport over the
///     <see cref="IImageJobCoordinator" />: it validates the prompt/model, hands a provider-neutral input to the
///     coordinator (which persists the job Queued with the prompt encrypted at rest and runs generation detached), then
///     returns the freshly-created Queued view. Operator-gated.
/// </summary>
public sealed class CreateImageJobEndpoint : Endpoint<CreateImageJobRequest, ImageJobResponse>
{
    private readonly IImageJobCoordinator _coordinator;
    private readonly ImageRuntimeOrchestrationService _imageRuntime;

    public CreateImageJobEndpoint(IImageJobCoordinator coordinator, ImageRuntimeOrchestrationService imageRuntime)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(imageRuntime);
        _coordinator = coordinator;
        _imageRuntime = imageRuntime;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.Jobs);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<CreateImageJobRequest>("application/json")
                               .Produces<ImageJobResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<ImageRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateImageJobRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            AddError("A prompt is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // The seed rides the wire as a precision-safe string; reject a non-integer value with a 400 before enqueue.
        if (!SeedValue.TryParse(req.Seed, out _, out var seedError))
        {
            AddError(seedError!);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        Guid jobId;
        try
        {
            jobId = await _coordinator.EnqueueAsync(req.ToInput(), ct);
        }
        catch (ImageRuntimeBusyException exception)
        {
            await Send.ResultAsync(ImageRuntimeBlockedEndpointSupport.RuntimeBusy(exception.Message, _imageRuntime.GetActivitySnapshot()));
            return;
        }

        var view = await _coordinator.GetAsync(jobId, ct);
        if (view is null)
        {
            // The coordinator just persisted the job; a null view here means an unexpected read miss.
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(view.ToResponse(), ct);
    }
}
