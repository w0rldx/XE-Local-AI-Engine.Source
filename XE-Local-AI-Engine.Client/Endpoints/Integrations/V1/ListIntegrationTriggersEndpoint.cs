namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>Every trigger, ordered by name. Operator-gated: an integrator never reads this surface.</summary>
public sealed class ListIntegrationTriggersEndpoint(IIntegrationTriggerService triggerService)
    : EndpointWithoutRequest<ListIntegrationTriggersResponse>
{
    private readonly IIntegrationTriggerService _triggerService = triggerService ?? throw new ArgumentNullException(nameof(triggerService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.Triggers);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var triggers = await _triggerService.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListIntegrationTriggersResponse
            {
                Items = triggers.Select(IntegrationMapper.ToView).ToArray()
            },
            ct).ConfigureAwait(false);
    }
}
