namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     <c>GET cloud-settings/entra/device-code/status</c> (Operator): reports the current Entra ID device-code
///     sign-in state. The UI polls this after starting a sign-in until it reaches a terminal state. Returns no token
///     material.
/// </summary>
public sealed class EntraDeviceCodeStatusEndpoint(IEntraDeviceCodeSignInCoordinator signInCoordinator)
    : EndpointWithoutRequest<EntraDeviceCodeSignInStatusResponse>
{
    private readonly IEntraDeviceCodeSignInCoordinator _signInCoordinator =
        signInCoordinator ?? throw new ArgumentNullException(nameof(signInCoordinator));

    public override void Configure()
    {
        Get(LocalApiRoutes.CloudSettings.EntraDeviceCodeStatus);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = _signInCoordinator.GetStatus();
        await Send.OkAsync(new EntraDeviceCodeSignInStatusResponse
        {
            State = status.State.ToString(),
            UserCode = status.UserCode,
            VerificationUri = status.VerificationUri,
            ExpiresAtUtc = status.ExpiresAtUtc
        }, ct).ConfigureAwait(false);
    }
}
