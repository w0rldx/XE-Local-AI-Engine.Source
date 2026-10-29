namespace XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Tutorial;

public sealed class SaveTutorialStateEndpoint : Endpoint<SaveTutorialStateRequest>
{
    private readonly INodeTutorialStateService _tutorialStateService;

    public SaveTutorialStateEndpoint(INodeTutorialStateService tutorialStateService)
    {
        ArgumentNullException.ThrowIfNull(tutorialStateService);
        _tutorialStateService = tutorialStateService;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Tutorial.State);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.ProducesProblemDetails(StatusCodes.Status409Conflict)
                                                   .AutoTagOverride("Tutorial"));
    }

    public override async Task HandleAsync(SaveTutorialStateRequest req, CancellationToken ct)
    {
        // SaveTutorialStateRequestValidator already refused every status the mapper cannot parse, so this reads the
        // same parse it read rather than a second copy of the rule.
        _ = TutorialStateMapper.TryParseStatus(req.Status, out var status);

        switch (await _tutorialStateService.SaveEntryAsync(User, req.Key, status, ct))
        {
            case TutorialEntrySaveOutcome.Saved:
                await Send.NoContentAsync(ct);
                return;
            case TutorialEntrySaveOutcome.NotPersisted:
                AddError("The tour progress could not be saved. Try again.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;
            default:
                await Send.UnauthorizedAsync(ct);
                return;
        }
    }
}
