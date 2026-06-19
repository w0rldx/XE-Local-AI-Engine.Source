namespace XE_Local_AI_Engine.Client.Endpoints.Cloud.Codex.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// <c>GET cloud/codex/status</c> (Operator): reports the current Codex session and login state. The UI
/// polls this after starting a login until <see cref="CodexStatusResponse.SignedIn"/> flips true (or the pending
/// login resolves). Returns no token material — only presence, the non-secret account id, the access-token
/// expiry, and whether a browser login is in flight.
///
/// <para>
/// <see cref="CodexStatusResponse.SignedIn"/> is gated on a <b>non-expired</b> (skew-adjusted) access token, so a
/// stale session does not report signed-in with a past <see cref="CodexStatusResponse.ExpiresAtUtc"/>. The
/// account id + expiry stay populated when a session exists so the UI can show a "session expired — re-authenticate"
/// state.
/// </para>
/// </summary>
public sealed class CodexStatusEndpoint(
    ICodexTokenStore tokenStore,
    ICodexLoginCoordinator loginCoordinator,
    IOptions<CodexOptions> codexOptions,
    TimeProvider timeProvider)
    : EndpointWithoutRequest<CodexStatusResponse>
{
    private readonly ICodexTokenStore _tokenStore =
        tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));

    private readonly ICodexLoginCoordinator _loginCoordinator =
        loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));

    private readonly CodexOptions _codexOptions =
        (codexOptions ?? throw new ArgumentNullException(nameof(codexOptions))).Value;

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Get(LocalApiRoutes.CloudCodex.Status);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var session = await _tokenStore.LoadAsync(ct).ConfigureAwait(false);
        var loginPending = _loginCoordinator.GetStatus().State == CodexLoginState.Pending;

        var response = session is null
            ? new CodexStatusResponse { SignedIn = false, LoginPending = loginPending }
            : new CodexStatusResponse
            {
                // Signed-in iff the access token is still valid (skew-adjusted); an expired session reports
                // SignedIn=false while keeping AccountId/ExpiresAtUtc so the UI can prompt re-authentication.
                SignedIn = !session.IsExpired(_codexOptions.ExpirySkew, _timeProvider.GetUtcNow()),
                AccountId = session.AccountId,
                ExpiresAtUtc = session.ExpiresUtc,
                LoginPending = loginPending,
            };

        await Send.OkAsync(response, ct).ConfigureAwait(false);
    }
}
