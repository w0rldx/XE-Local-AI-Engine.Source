namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Reports whether work sessions are switched on for this node.
///     <para>
///         The one route in the family carved out of the disabled-node 404 sweep in <c>Program</c>, exactly as
///         <c>development/capability</c> is: every other path under <c>work-sessions/</c> answers a bodyless 404 with
///         the feature off, which a client cannot tell from a broken route. This endpoint is what lets the SPA say
///         "switched off on this node" rather than "could not load work sessions".
///     </para>
///     <para>
///         The literal <c>capability</c> segment outranks the <c>{sessionId}</c> parameter in route matching, so this
///         route can never be reached as a session lookup. Constructor injection is safe with the feature off because
///         <see cref="WorkSessionOptions" /> is bound unconditionally — this family is never dropped from discovery.
///     </para>
/// </summary>
public sealed class GetWorkSessionCapabilityEndpoint : EndpointWithoutRequest<WorkSessionCapabilityResponse>
{
    private readonly IOptions<WorkSessionOptions> _options;

    public GetWorkSessionCapabilityEndpoint(IOptions<WorkSessionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Capability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new WorkSessionCapabilityResponse(_options.Value.Enabled), ct);
}
