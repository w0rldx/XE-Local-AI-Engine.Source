namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     The legacy-install step: the signed-in admin re-enters the password, Identity verifies it, and the pre-vault
///     <c>node.key</c> is wrapped under it. Answers the one-time recovery code; 409 unless the vault is pending.
/// </summary>
public sealed class NodeVaultConfirmEndpoint : Endpoint<NodeVaultConfirmRequest, NodeVaultConfirmResponse>
{
    private readonly INodeAuthService _authService;

    public NodeVaultConfirmEndpoint(INodeAuthService authService)
    {
        _authService = authService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Auth.VaultConfirm);
        Policies(NodeAuthorizationPolicies.Operator);
        // It verifies the password, so it shares the setup/login throttle like change-password does.
        Options(static options => options.RequireRateLimiting(NodeAuthRateLimits.AuthPolicy));
        Description(static descriptor => descriptor.AutoTagOverride("Auth"));
    }

    public override async Task HandleAsync(NodeVaultConfirmRequest req, CancellationToken ct)
    {
        var result = await _authService.ConfirmLegacyVaultAsync(User, req.Password, ct);
        if (result.NotPending)
        {
            await Send.ResultAsync(Results.Conflict(new NodeAuthErrorResponse
            {
                Message = "The node vault is not waiting for a password confirmation."
            }));
            return;
        }

        if (!result.Succeeded || result.RecoveryCode is null)
        {
            await Send.ResultAsync(Results.BadRequest(new NodeAuthErrorResponse
            {
                Message = "Vault confirmation failed.",
                Errors = result.Errors
            }));
            return;
        }

        await Send.OkAsync(new NodeVaultConfirmResponse
        {
            RecoveryCode = result.RecoveryCode
        }, ct);
    }
}
