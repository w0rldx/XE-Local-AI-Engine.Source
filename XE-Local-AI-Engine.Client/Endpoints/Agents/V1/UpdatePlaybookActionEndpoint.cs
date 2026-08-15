namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class UpdatePlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    : Endpoint<UpdatePlaybookActionRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public override void Configure()
    {
        Put(LocalApiRoutes.Agents.PlaybookActionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdatePlaybookActionRequest req, CancellationToken ct)
    {
        var record = await _playbookActionService.UpdateAsync(req.ActionId, req.ToInput(), ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}
