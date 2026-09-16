namespace XE_Local_AI_Engine.Client.Endpoints.Cloud.Codex.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     <c>POST cloud/codex/logout</c> (Operator): clears the stored Codex OAuth session (deletes
///     <c>codex-oauth-tokens.enc</c>), so the next chat send routes back to Azure-or-local. Returns the resulting
///     signed-out status. Never returns token material.
/// </summary>
public sealed class CodexLogoutEndpoint(CodexSessionService session)
    : EndpointWithoutRequest<CodexStatusResponse>
{
    private readonly CodexSessionService _session = session ?? throw new ArgumentNullException(nameof(session));

    public override void Configure()
    {
        Post(LocalApiRoutes.CloudCodex.Logout);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _session.SignOutAsync(ct).ConfigureAwait(false);

        await Send.OkAsync(new CodexStatusResponse
        {
            SignedIn = false,
            LoginPending = false
        }, ct).ConfigureAwait(false);
    }
}
