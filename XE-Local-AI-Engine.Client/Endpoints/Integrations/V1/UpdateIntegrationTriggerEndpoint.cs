namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Updates a trigger under optimistic concurrency. The external <c>Name</c> is deliberately not editable: it is the
///     contract a caller addresses, so changing it is a delete-and-create decision.
/// </summary>
public sealed class UpdateIntegrationTriggerEndpoint(IIntegrationTriggerService triggerService)
    : Endpoint<UpdateIntegrationTriggerRequest, IntegrationTriggerView>
{
    private readonly IIntegrationTriggerService _triggerService = triggerService ?? throw new ArgumentNullException(nameof(triggerService));

    public override void Configure()
    {
        Put(LocalApiRoutes.Integrations.TriggerById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateIntegrationTriggerRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var acceptedInputKinds = IntegrationMapper.FromWireInputKinds(req.AcceptedInputKinds).GetValueOrDefault();

        var result = await _triggerService.UpdateAsync(Route<Guid>("triggerId"),
                                              new IntegrationTriggerUpdateInput(req.ExpectedVersion,
                                                  req.DisplayName,
                                                  req.Description,
                                                  req.Enabled,
                                                  req.TargetAgentDefinitionId,
                                                  req.SessionPolicy,
                                                  acceptedInputKinds),
                                              ct)
                                          .ConfigureAwait(false);

        if (result.Outcome != IntegrationTriggerOutcome.Saved)
        {
            await IntegrationTriggerResponses.SendFailureAsync(this, Send, result, ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(IntegrationMapper.ToView(result.Trigger!), ct).ConfigureAwait(false);
    }
}
