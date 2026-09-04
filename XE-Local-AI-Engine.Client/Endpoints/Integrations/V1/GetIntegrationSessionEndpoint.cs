namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     One session for the operator. Deliberately NOT principal-scoped: an operator reading the admin surface is not
///     acting as an integrator, so a missing row is a plain 404 rather than the external family's masked one.
/// </summary>
public sealed class GetIntegrationSessionEndpoint(IntegrationSessionService sessions)
    : EndpointWithoutRequest<IntegrationSessionResponse>
{
    private readonly IntegrationSessionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.SessionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var session = await _sessions.GetAsync(Route<Guid>("sessionId"), ct).ConfigureAwait(false);
        if (session is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(IntegrationMapper.ToResponse(session), ct).ConfigureAwait(false);
    }
}
