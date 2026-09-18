namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class CreatePlaybookActionEndpoint : Endpoint<CreatePlaybookActionRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService;

    public CreatePlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    {
        ArgumentNullException.ThrowIfNull(playbookActionService);
        _playbookActionService = playbookActionService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.Playbook);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreatePlaybookActionRequest req, CancellationToken ct)
    {
        var record = await _playbookActionService.CreateAsync(req.ToInput(), ct);
        await Send.OkAsync(record.ToResponse(), ct);
    }
}
