namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     The one mapping from a trigger-write outcome to an HTTP response, shared by the create and the update so the
///     two cannot drift.
/// </summary>
/// <remarks>
///     The service reports these as return values rather than exceptions — none of them is exceptional — which is why
///     the endpoint writes the body itself. The endpoint passes both itself (for <c>AddError</c>) and its <c>Send</c>
///     sender, which is protected and therefore not reachable from here: the shape
///     <c>SelectedFolderEndpointSupport</c> established.
/// </remarks>
internal static class IntegrationTriggerResponses
{
    public static Task SendFailureAsync<TRequest, TResponse>(IValidationErrors errors,
        ResponseSender<TRequest, TResponse> send,
        IntegrationTriggerResult.Rejected rejected,
        CancellationToken ct)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(rejected);

        errors.AddError(rejected.Message);
        switch (rejected.Outcome)
        {
            case IntegrationTriggerOutcome.NameConflict:
            case IntegrationTriggerOutcome.VersionConflict:
                return send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);

            case IntegrationTriggerOutcome.AgentMissing:
            case IntegrationTriggerOutcome.TargetKindRejected:
            case IntegrationTriggerOutcome.GraphWorkflowMissing:
            case IntegrationTriggerOutcome.GraphWorkflowsDisabled:
                return send.ErrorsAsync(cancellation: ct);

            default:
                // Reached only if a new outcome is added without a branch here, which is a wiring bug rather than a
                // request the caller can make. Loud beats a silent 200 with no body.
                throw new InvalidOperationException($"Integration trigger outcome '{rejected.Outcome}' has no response mapping.");
        }
    }
}
