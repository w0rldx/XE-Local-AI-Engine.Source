namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Polls the in-flight GitHub device flow (POST github-auth/poll). Reads the secret <c>device_code</c> from the
///     server-side <see cref="IGitHubDeviceFlowSession" /> (never from React), exchanges once via
///     <see cref="IGitHubAuthService.PollAsync" />, and reports the state. On <c>authorized</c> the token is already
///     stored by the service and only the login is returned; the device code is cleared on any terminal state.
/// </summary>
public sealed class PollGitHubAuthEndpoint(IGitHubAuthService authService, IGitHubDeviceFlowSession deviceFlowSession)
    : EndpointWithoutRequest<GitHubAuthPollResponse>, IDesktopOnlyEndpoint
{
    private readonly IGitHubAuthService _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    private readonly IGitHubDeviceFlowSession _deviceFlowSession = deviceFlowSession ?? throw new ArgumentNullException(nameof(deviceFlowSession));

    public override void Configure()
    {
        Post(LocalApiRoutes.GitHubAuth.Poll);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var deviceCode = _deviceFlowSession.PendingDeviceCode;
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            // No flow in progress — treat as expired so the client restarts the flow.
            await Send.OkAsync(new GitHubAuthPollResponse
                {
                    State = "expired"
                },
                ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var poll = await _authService.PollAsync(deviceCode, ct).ConfigureAwait(false);

            // Clear the pending device code on any terminal state so a stale code is never replayed.
            if (poll.State is not GitHubDeviceFlowState.Pending)
            {
                _deviceFlowSession.Clear();
            }

            await Send.OkAsync(new GitHubAuthPollResponse
                {
                    State = ToWireState(poll.State),
                    Login = poll.State is GitHubDeviceFlowState.Authorized ? poll.Login : null
                },
                ct).ConfigureAwait(false);
        }
        catch (GitHubAuthException exception)
        {
            _deviceFlowSession.Clear();
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }

    private static string ToWireState(GitHubDeviceFlowState state) => state switch
    {
        GitHubDeviceFlowState.Pending => "pending",
        GitHubDeviceFlowState.Authorized => "authorized",
        GitHubDeviceFlowState.Denied => "denied",
        _ => "expired"
    };
}
