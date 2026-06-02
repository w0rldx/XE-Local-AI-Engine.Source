namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class CreatePlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    : Endpoint<CreatePlaybookActionRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.Playbook);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreatePlaybookActionRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _playbookActionService.CreateAsync(req.ToInput(), ct).ConfigureAwait(false);
            await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (PlaybookActionValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
