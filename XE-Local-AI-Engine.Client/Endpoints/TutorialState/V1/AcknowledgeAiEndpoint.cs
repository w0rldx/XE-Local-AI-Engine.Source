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
        Description(static descriptor => descriptor.AutoTagOverride("Tutorial"));
        Summary(static summary =>
        {
            summary.Summary = "Records that the caller acknowledged the AI responsibility notice.";
            summary.Description = "Idempotent: a repeat keeps the first acknowledgement's timestamp.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var acknowledgedAt = await _tutorialStateService.AcknowledgeAiAsync(User, ct);
        if (acknowledgedAt is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.OkAsync(new AiAcknowledgementResponse
            {
                Acknowledged = true,
                AcknowledgedAtUtc = acknowledgedAt
            },
            ct);
    }
}
