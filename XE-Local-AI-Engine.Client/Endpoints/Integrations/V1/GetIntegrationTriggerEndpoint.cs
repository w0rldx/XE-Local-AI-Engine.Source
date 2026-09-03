namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>One trigger by id. Also the <c>Location</c> target of the create.</summary>
public sealed class GetIntegrationTriggerEndpoint(IIntegrationTriggerService triggerService)
    : EndpointWithoutRequest<IntegrationTriggerView>
{
    private readonly IIntegrationTriggerService _triggerService = triggerService ?? throw new ArgumentNullException(nameof(triggerService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.TriggerById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var trigger = await _triggerService.GetAsync(Route<Guid>("triggerId"), ct).ConfigureAwait(false);
        if (trigger is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(IntegrationMapper.ToView(trigger), ct).ConfigureAwait(false);
    }
}
