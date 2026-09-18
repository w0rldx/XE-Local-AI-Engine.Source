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
        Description(static descriptor => descriptor.AutoTagOverride("Tutorial"));
    }

    // Bounds the persisted key so an authenticated operator cannot bloat the identity row with an oversized key.
    private const int MaxKeyLength = 128;

    public override async Task HandleAsync(SaveTutorialStateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Key))
        {
            AddError(r => r.Key, "Key is required.");
        }
        else if (req.Key.Trim().Length > MaxKeyLength)
        {
            AddError(r => r.Key, $"Key must be {MaxKeyLength} characters or fewer.");
        }

        if (!TutorialStateMapper.TryParseStatus(req.Status, out var status))
        {
            AddError(r => r.Status, "Status must be 'completed' or 'skipped'.");
        }

        ThrowIfAnyErrors();

        var saved = await _tutorialStateService.SaveEntryAsync(User, req.Key, status, ct);
        if (!saved)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
