namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     The one mapping from a trigger-write outcome to an HTTP response, shared by the create and the update so the two
///     cannot drift. The service reports these as return values rather than exceptions — none of them is exceptional —
///     which is why the endpoint writes the body itself.
///     <para>
///         The endpoint passes both itself (for <c>AddError</c>) and its <c>Send</c> sender, which is protected and
///         therefore not reachable from here — the shape <c>SelectedFolderEndpointSupport</c> established.
///     </para>
/// </summary>
internal static class IntegrationTriggerResponses
{
    public static Task SendFailureAsync<TRequest, TResponse>(IValidationErrors errors,
        ResponseSender<TRequest, TResponse> send,
        IntegrationTriggerResult result,
        CancellationToken ct)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case IntegrationTriggerOutcome.NotFound:
                return send.NotFoundAsync(ct);

            case IntegrationTriggerOutcome.NameConflict:
            case IntegrationTriggerOutcome.VersionConflict:
                errors.AddError(result.Message ?? "The trigger could not be saved.");
                return send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);

            case IntegrationTriggerOutcome.AgentMissing:
            case IntegrationTriggerOutcome.SessionPolicyRejected:
            case IntegrationTriggerOutcome.TargetKindRejected:
                errors.AddError(result.Message ?? "The trigger could not be saved.");
                return send.ErrorsAsync(cancellation: ct);

            case IntegrationTriggerOutcome.Saved:
            default:
                // Reached only if a new outcome is added without a branch here, which is a wiring bug rather than a
                // request the caller can make. Loud beats a silent 200 with no body.
                throw new InvalidOperationException($"Integration trigger outcome '{result.Outcome}' has no response mapping.");
        }
    }
}
