namespace XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Tutorial;

public sealed class AcknowledgeAiEndpoint : EndpointWithoutRequest<AiAcknowledgementResponse>
{
    private readonly INodeTutorialStateService _tutorialStateService;

    public AcknowledgeAiEndpoint(INodeTutorialStateService tutorialStateService)
    {
        ArgumentNullException.ThrowIfNull(tutorialStateService);
        _tutorialStateService = tutorialStateService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Tutorial.AiAcknowledgement);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.ProducesProblemDetails(StatusCodes.Status409Conflict)
                                                   .AutoTagOverride("Tutorial"));
        Summary(static summary =>
        {
            summary.Summary = "Records that the caller acknowledged the AI responsibility notice.";
            summary.Description = "Idempotent: a repeat keeps the first acknowledgement's timestamp.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        switch (await _tutorialStateService.AcknowledgeAiAsync(User, ct))
        {
            case AiAcknowledgementResult.Acknowledged acknowledged:
                await Send.OkAsync(new AiAcknowledgementResponse
                    {
                        Acknowledged = true,
                        AcknowledgedAtUtc = acknowledged.AtUtc
                    },
                    ct);
                return;
            case AiAcknowledgementResult.NotPersisted:
                AddError("The acknowledgement could not be saved. Try again.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;
            default:
                await Send.UnauthorizedAsync(ct);
                return;
        }
    }
}
