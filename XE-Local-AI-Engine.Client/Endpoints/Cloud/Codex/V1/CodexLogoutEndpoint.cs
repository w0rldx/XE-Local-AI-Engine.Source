namespace XE_Local_AI_Engine.Client.Endpoints.Cloud.Codex.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// <c>POST cloud/codex/logout</c> (Operator): clears the stored Codex OAuth session (deletes
/// <c>codex-oauth-tokens.enc</c>), so the next chat send routes back to Azure-or-local (plan §8, the C2 sign-out
/// path). Returns the resulting signed-out status. Never returns token material.
/// </summary>
public sealed class CodexLogoutEndpoint(
    ICodexTokenStore tokenStore,
    IActiveCloudChatClientFactory activeCloudFactory)
    : EndpointWithoutRequest<CodexStatusResponse>
{
    private readonly ICodexTokenStore _tokenStore =
        tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));

    private readonly IActiveCloudChatClientFactory _activeCloudFactory =
        activeCloudFactory ?? throw new ArgumentNullException(nameof(activeCloudFactory));

    public override void Configure()
    {
        Post(LocalApiRoutes.CloudCodex.Logout);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _tokenStore.ClearAsync(ct).ConfigureAwait(false);

        // Invalidate the selector's snapshot so the very next send reverts to Azure/local without waiting for the TTL.
        _activeCloudFactory.InvalidateSelectionCache();

        await Send.OkAsync(new CodexStatusResponse { SignedIn = false, LoginPending = false }, ct).ConfigureAwait(false);
    }
}
