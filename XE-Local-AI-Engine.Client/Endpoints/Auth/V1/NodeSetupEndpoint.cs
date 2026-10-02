namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class NodeSetupEndpoint : Endpoint<NodeSetupRequest, NodeSetupResponse>
{
    private readonly INodeAuthService _authService;

    public NodeSetupEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.Setup);
        AllowAnonymous();
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(NodeSetupRequest req, CancellationToken ct)
    {
        var result = await _authService.SetupAsync(req.Email, req.Password, ct);
        if (result.Succeeded)
        {
            // The recovery code is in this one response and nowhere else: the SPA shows it once, the node never stores it.
            await Send.OkAsync(new NodeSetupResponse
            {
                RecoveryCode = result.RecoveryCode
            }, ct);
            return;
        }

        if (result.AlreadyInitialized)
        {
            await Send.ResultAsync(Results.Conflict(new NodeAuthErrorResponse
            {
                Message = "Node auth is already configured."
            }));
            return;
        }

        await Send.ResultAsync(Results.BadRequest(new NodeAuthErrorResponse
        {
            Message = "Node auth setup failed.",
            Errors = result.Errors
        }));
    }
}
