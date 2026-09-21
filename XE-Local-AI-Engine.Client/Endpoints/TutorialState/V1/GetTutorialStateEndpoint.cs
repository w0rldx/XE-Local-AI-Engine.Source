namespace XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Tutorial;

public sealed class GetTutorialStateEndpoint : EndpointWithoutRequest<TutorialStateResponse>
{
    private readonly INodeTutorialStateService _tutorialStateService;

    public GetTutorialStateEndpoint(INodeTutorialStateService tutorialStateService)
    {
        ArgumentNullException.ThrowIfNull(tutorialStateService);
        _tutorialStateService = tutorialStateService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Tutorial.State);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Tutorial"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var entries = await _tutorialStateService.GetEntriesAsync(User, ct);
        await Send.OkAsync(entries.ToResponse(), ct);
    }
}
