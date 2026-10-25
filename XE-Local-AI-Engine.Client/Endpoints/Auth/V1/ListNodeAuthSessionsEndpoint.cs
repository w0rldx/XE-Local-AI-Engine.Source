namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class ListNodeAuthSessionsEndpoint : EndpointWithoutRequest<IReadOnlyList<NodeAuthSessionResponse>>
{
    private readonly INodeAuthService _authService;

    public ListNodeAuthSessionsEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Auth.Sessions);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
        Summary(static summary => summary.Summary = "Lists the caller's live sign-in sessions; the current one is flagged.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var currentSessionId = User.FindFirst(JwtRegisteredClaimNames.Sid)?.Value;
        var sessions = await _authService.ListSessionsAsync(User, ct);

        await Send.OkAsync(sessions.Select(session => new NodeAuthSessionResponse
                                    {
                                        Id = session.Id,
                                        ClientLabel = session.ClientLabel,
                                        // SQLite hands the instants back unspecified; marked UTC so the wire carries the Z.
                                        CreatedAtUtc = DateTime.SpecifyKind(session.CreatedAtUtc, DateTimeKind.Utc),
                                        LastUsedAtUtc = DateTime.SpecifyKind(session.LastUsedAtUtc, DateTimeKind.Utc),
                                        Current = string.Equals(session.Id, currentSessionId, StringComparison.Ordinal)
                                    })
                                   .ToList(),
            ct);
    }
}
