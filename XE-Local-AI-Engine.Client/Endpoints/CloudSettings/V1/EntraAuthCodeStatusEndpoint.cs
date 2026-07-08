namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     <c>GET cloud-settings/entra/auth-code/status</c> (Operator): reports the current Entra ID authorization-code
///     sign-in state. The UI polls this after starting a sign-in until it reaches a terminal state. Returns no token
///     material.
/// </summary>
public sealed class EntraAuthCodeStatusEndpoint(IEntraAuthCodeSignInCoordinator signInCoordinator)
    : EndpointWithoutRequest<EntraAuthCodeSignInStatusResponse>
{
    private readonly IEntraAuthCodeSignInCoordinator _signInCoordinator =
        signInCoordinator ?? throw new ArgumentNullException(nameof(signInCoordinator));

    public override void Configure()
    {
        Get(LocalApiRoutes.CloudSettings.EntraAuthCodeStatus);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = _signInCoordinator.GetStatus();
        await Send.OkAsync(new EntraAuthCodeSignInStatusResponse
        {
            State = status.State.ToString(),
            ExpiresAtUtc = status.ExpiresAtUtc
        }, ct).ConfigureAwait(false);
    }
}
