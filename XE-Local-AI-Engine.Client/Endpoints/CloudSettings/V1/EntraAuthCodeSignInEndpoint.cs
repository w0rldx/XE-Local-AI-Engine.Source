namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     <c>POST cloud-settings/entra/auth-code/start</c> (Operator): starts (or supersedes) the Entra ID
///     authorization-code sign-in flow for the stored Azure Foundry connection and returns the authorize URL to
///     open in a browser. The token exchange completes in the background — the UI polls
///     <c>cloud-settings/entra/auth-code/status</c> for completion. Never returns token material.
/// </summary>
public sealed class EntraAuthCodeSignInEndpoint(IEntraAuthCodeSignInCoordinator signInCoordinator)
    : EndpointWithoutRequest<EntraAuthCodeSignInResponse>
{
    private readonly IEntraAuthCodeSignInCoordinator _signInCoordinator =
        signInCoordinator ?? throw new ArgumentNullException(nameof(signInCoordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.CloudSettings.EntraAuthCodeStart);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var handle = await _signInCoordinator.StartAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(new EntraAuthCodeSignInResponse
            {
                AuthorizeUrl = handle.AuthorizeUrl,
                ExpiresAtUtc = handle.ExpiresAtUtc
            }, ct).ConfigureAwait(false);
        }
        catch (EntraConnectionNotConfiguredException exception)
        {
            // Only the user-actionable "no Entra connection configured" precondition is surfaced as a 400 with its
            // (path-free, safe) message. Every other failure flows to the global handlers for a clean 500 — the
            // previous catch of the base InvalidOperationException swallowed unexpected faults and leaked their raw
            // messages.
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
