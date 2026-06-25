namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Reports the current GitHub sign-in state (GET github-auth/status). Combines the stored session presence
///     (<see cref="IGitHubTokenStore" />) with the last check's auth state from <see cref="IAppUpdateState" /> so a
///     server-side revoke (recorded as <c>reauthRequired</c>) / lost repo access (<c>noAccess</c>) surfaces here. NEVER
///     returns the token — only the state + login.
/// </summary>
public sealed class GetGitHubAuthStatusEndpoint(IGitHubTokenStore tokenStore, IAppUpdateState updateState)
    : EndpointWithoutRequest<GitHubAuthStatusResponse>, IDesktopOnlyEndpoint
{
    private readonly IGitHubTokenStore _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    private readonly IAppUpdateState _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));

    public override void Configure()
    {
        Get(LocalApiRoutes.GitHubAuth.Status);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var session = await _tokenStore.GetSessionAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            await Send.OkAsync(new GitHubAuthStatusResponse
                {
                    AuthState = AppUpdateAuthStateWire.Of(AppUpdateAuthState.SignedOut)
                },
                ct).ConfigureAwait(false);
            return;
        }

        // A session is stored, so surface the last check's degraded auth state when it indicates the token was rejected
        // or the user lost repo access; otherwise report the signed-in state.
        var snapshotAuth = _updateState.Current.AuthState;
        var authState = snapshotAuth is AppUpdateAuthState.ReauthRequired or AppUpdateAuthState.NoAccess
            ? snapshotAuth
            : AppUpdateAuthState.SignedIn;

        await Send.OkAsync(new GitHubAuthStatusResponse
            {
                AuthState = AppUpdateAuthStateWire.Of(authState),
                Login = session.Login
            },
            ct).ConfigureAwait(false);
    }
}
