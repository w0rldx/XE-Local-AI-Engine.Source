namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>Every trigger, ordered by name. Operator-gated: an integrator never reads this surface.</summary>
public sealed class ListIntegrationTriggersEndpoint : EndpointWithoutRequest<ListIntegrationTriggersResponse>
{
    private readonly IIntegrationTriggerService _triggerService;

    public ListIntegrationTriggersEndpoint(IIntegrationTriggerService triggerService)
    {
        ArgumentNullException.ThrowIfNull(triggerService);
        _triggerService = triggerService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.Triggers);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var triggers = await _triggerService.ListAsync(ct);
        await Send.OkAsync(new ListIntegrationTriggersResponse
            {
                Items = triggers.Select(IntegrationMapper.ToView).ToArray()
            },
            ct);
    }
}
