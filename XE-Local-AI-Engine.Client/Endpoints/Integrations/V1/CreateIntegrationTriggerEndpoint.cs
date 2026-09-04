namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Creates a trigger. 400 when the target agent is missing or a caller-managed policy names an agent whose tools
///     are not read-only; 409 when the normalised name is already taken.
/// </summary>
public sealed class CreateIntegrationTriggerEndpoint(IIntegrationTriggerService triggerService)
    : Endpoint<CreateIntegrationTriggerRequest, IntegrationTriggerView>
{
    private readonly IIntegrationTriggerService _triggerService = triggerService ?? throw new ArgumentNullException(nameof(triggerService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Integrations.Triggers);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateIntegrationTriggerRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The validator already rejected an empty or unknown-member array, so a null here is unreachable; the
        // fallback keeps the decoder as the single source of the mapping rather than duplicating it.
        var acceptedInputKinds = IntegrationMapper.FromWireInputKinds(req.AcceptedInputKinds).GetValueOrDefault();

        var result = await _triggerService.CreateAsync(new IntegrationTriggerCreateInput(req.Name,
                                              req.DisplayName,
                                              req.Description,
                                              req.Enabled,
                                              req.TargetKind,
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

        var view = IntegrationMapper.ToView(result.Trigger!);
        await Send.CreatedAtAsync<GetIntegrationTriggerEndpoint>(new
            {
                triggerId = view.Id
            },
            view,
            cancellation: ct).ConfigureAwait(false);
    }
}
