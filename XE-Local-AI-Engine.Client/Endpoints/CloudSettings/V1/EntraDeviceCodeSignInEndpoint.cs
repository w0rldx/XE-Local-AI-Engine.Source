namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     <c>POST cloud-settings/entra/device-code/start</c> (Operator): starts (or supersedes) the Entra ID device-code
///     sign-in flow for the stored Azure Foundry connection and returns the user code + verification URL so the UI
///     can render a copyable/clickable link. The token exchange completes in the background — the UI polls
///     <c>cloud-settings/entra/device-code/status</c> for completion. Never returns token material.
/// </summary>
public sealed class EntraDeviceCodeSignInEndpoint(IEntraDeviceCodeSignInCoordinator signInCoordinator)
    : EndpointWithoutRequest<EntraDeviceCodeSignInResponse>
{
    private readonly IEntraDeviceCodeSignInCoordinator _signInCoordinator =
        signInCoordinator ?? throw new ArgumentNullException(nameof(signInCoordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.CloudSettings.EntraDeviceCodeStart);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var handle = await _signInCoordinator.StartAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(new EntraDeviceCodeSignInResponse
            {
                UserCode = handle.UserCode,
                VerificationUri = handle.VerificationUri,
                ExpiresAtUtc = handle.ExpiresAtUtc
            }, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
