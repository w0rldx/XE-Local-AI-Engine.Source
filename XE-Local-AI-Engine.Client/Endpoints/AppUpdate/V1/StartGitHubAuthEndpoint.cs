namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Begins the GitHub App device flow (POST github-auth/start). Calls <see cref="IGitHubAuthService.StartAsync" />,
///     stores the secret <c>device_code</c> server-side via <see cref="IGitHubDeviceFlowSession" />, and returns ONLY the
///     user code + verification URI to React. The device_code is NEVER returned.
/// </summary>
public sealed class StartGitHubAuthEndpoint(IGitHubAuthService authService, IGitHubDeviceFlowSession deviceFlowSession)
    : EndpointWithoutRequest<GitHubAuthStartResponse>, IDesktopOnlyEndpoint
{
    private readonly IGitHubAuthService _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    private readonly IGitHubDeviceFlowSession _deviceFlowSession = deviceFlowSession ?? throw new ArgumentNullException(nameof(deviceFlowSession));

    public override void Configure()
    {
        Post(LocalApiRoutes.GitHubAuth.Start);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var start = await _authService.StartAsync(ct).ConfigureAwait(false);

            // Hold the secret device_code server-side; poll reads it from here, never from the client.
            _deviceFlowSession.Begin(start.DeviceCode);

            await Send.OkAsync(new GitHubAuthStartResponse
                {
                    UserCode = start.UserCode,
                    VerificationUri = start.VerificationUri,
                    ExpiresInSeconds = start.ExpiresInSeconds,
                    IntervalSeconds = start.IntervalSeconds
                },
                ct).ConfigureAwait(false);
        }
        catch (GitHubAuthException exception)
        {
            // Sanitized message (no token / device_code / internal URL).
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
