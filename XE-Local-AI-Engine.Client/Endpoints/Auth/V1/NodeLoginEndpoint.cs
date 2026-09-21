namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeLoginEndpoint : Endpoint<NodeLoginRequest, NodeAccessTokenResponse>
{
    private readonly INodeAuthService _authService;

    public NodeLoginEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Login);
        AllowAnonymous();
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));

        // The locked-out 401 carries a body where a wrong-password 401 carries none, so it has to be in the contract:
        // without it the generated client types the 401 as empty and the SPA has to hand-write the shape it reads.
        Description(static descriptor => descriptor.Produces<NodeLoginLockedOutResponse>(StatusCodes.Status401Unauthorized)
                                                   .AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(NodeLoginRequest req, CancellationToken ct)
    {
        var result = await _authService.LoginAsync(req.Email, req.Password, ct);
        await NodeAuthEndpointSupport.SendTokenResultAsync(Send, result, ct);
    }
}
