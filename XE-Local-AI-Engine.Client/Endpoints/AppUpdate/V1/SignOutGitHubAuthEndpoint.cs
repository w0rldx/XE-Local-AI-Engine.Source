namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Signs out of GitHub (POST github-auth/sign-out). Delegates to <see cref="IGitHubAuthService.SignOutAsync" />, which
///     attempts a best-effort server-side revoke (a no-op without the App client_secret the device flow does not carry)
///     and then deletes the local token — the deletion is the guaranteed effect. It then drops any in-flight device flow
///     and returns the resulting signed-out status. A user who suspects the token is compromised should also revoke it at
///     github.com/settings.
/// </summary>
public sealed class SignOutGitHubAuthEndpoint(IGitHubAuthService authService, IGitHubDeviceFlowSession deviceFlowSession)
    : EndpointWithoutRequest<GitHubAuthStatusResponse>, IDesktopOnlyEndpoint
{
    private readonly IGitHubAuthService _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    private readonly IGitHubDeviceFlowSession _deviceFlowSession = deviceFlowSession ?? throw new ArgumentNullException(nameof(deviceFlowSession));

    public override void Configure()
    {
        Post(LocalApiRoutes.GitHubAuth.SignOut);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _authService.SignOutAsync(ct).ConfigureAwait(false);
        _deviceFlowSession.Clear();

        await Send.OkAsync(new GitHubAuthStatusResponse
            {
                AuthState = AppUpdateAuthStateWire.Of(AppUpdateAuthState.SignedOut)
            },
            ct).ConfigureAwait(false);
    }
}
