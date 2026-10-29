namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class UpdatePlaybookActionEndpoint : Endpoint<UpdatePlaybookActionRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService;

    public UpdatePlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    {
        ArgumentNullException.ThrowIfNull(playbookActionService);
        _playbookActionService = playbookActionService;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Agents.PlaybookActionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdatePlaybookActionRequest req, CancellationToken ct)
    {
        switch (await _playbookActionService.UpdateAsync(req.ActionId, req.ToInput(), ct))
        {
            case PlaybookActionUpdateResult.Updated updated:
                await Send.OkAsync(updated.Record.ToResponse(), ct);
                return;
            default:
                // The `outcome` member keeps the refusal machine-readable without matching on the message.
                await Send.ConflictProblemAsync("This action came from analysis; edit it through the suggestion review instead.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["outcome"] = "NotManual"
                    });
                return;
        }
    }
}
