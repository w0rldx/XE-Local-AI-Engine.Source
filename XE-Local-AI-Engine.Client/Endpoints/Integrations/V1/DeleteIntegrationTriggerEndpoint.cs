namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Deletes a trigger. A HARD delete: past executions keep their own trigger id, so the history outlives the
///     definition, and a caller that invokes the removed name gets the ordinary 404.
/// </summary>
public sealed class DeleteIntegrationTriggerEndpoint(IIntegrationTriggerService triggerService)
    : EndpointWithoutRequest
{
    private readonly IIntegrationTriggerService _triggerService = triggerService ?? throw new ArgumentNullException(nameof(triggerService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Integrations.TriggerById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await _triggerService.DeleteAsync(Route<Guid>("triggerId"), ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
