namespace XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Tutorial;

public sealed class GetAiAcknowledgementEndpoint : EndpointWithoutRequest<AiAcknowledgementResponse>
{
    private readonly INodeTutorialStateService _tutorialStateService;

    public GetAiAcknowledgementEndpoint(INodeTutorialStateService tutorialStateService)
    {
        ArgumentNullException.ThrowIfNull(tutorialStateService);
        _tutorialStateService = tutorialStateService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Tutorial.AiAcknowledgement);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Tutorial"));
        Summary(static summary => summary.Summary = "Reads whether the caller acknowledged the AI responsibility notice.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var acknowledgedAt = await _tutorialStateService.GetAiAcknowledgedAtAsync(User, ct);
        await Send.OkAsync(new AiAcknowledgementResponse
        {
            Acknowledged = acknowledgedAt is not null,
            AcknowledgedAtUtc = acknowledgedAt
        },
            ct);
    }
}
