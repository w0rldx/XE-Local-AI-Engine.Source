namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>One trigger by id. Also the <c>Location</c> target of the create.</summary>
public sealed class GetIntegrationTriggerEndpoint : EndpointWithoutRequest<IntegrationTriggerView>
{
    private readonly IIntegrationTriggerService _triggerService;

    public GetIntegrationTriggerEndpoint(IIntegrationTriggerService triggerService)
    {
        ArgumentNullException.ThrowIfNull(triggerService);
        _triggerService = triggerService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.TriggerById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var trigger = await _triggerService.GetAsync(Route<Guid>("triggerId"), ct);
        if (trigger is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(IntegrationMapper.ToView(trigger), ct);
    }
}
