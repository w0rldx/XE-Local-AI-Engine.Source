namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     Starts (or supersedes) the Entra ID authorization-code sign-in flow for the stored Azure Foundry connection
///     and returns the authorize URL to open in a browser. Operator-gated.
/// </summary>
/// <remarks>
///     The token exchange completes in the background; the UI polls <c>cloud-settings/entra/auth-code/status</c> for
///     completion. Never returns token material.
/// </remarks>
public sealed class EntraAuthCodeSignInEndpoint : EndpointWithoutRequest<EntraAuthCodeSignInResponse>
{
    private readonly IEntraAuthCodeSignInCoordinator _signInCoordinator;

    public EntraAuthCodeSignInEndpoint(IEntraAuthCodeSignInCoordinator signInCoordinator)
    {
        ArgumentNullException.ThrowIfNull(signInCoordinator);
        _signInCoordinator = signInCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.CloudSettings.EntraAuthCodeStart);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Only the user-actionable "no Entra connection configured" precondition is surfaced as a 400, with its path-free message, by DomainValidationExceptionHandler.
        // Every other failure flows to the global handlers for a clean 500: catching the base InvalidOperationException here would swallow it and leak its raw message.
        var handle = await _signInCoordinator.StartAsync(ct);
        await Send.OkAsync(new EntraAuthCodeSignInResponse
        {
            AuthorizeUrl = handle.AuthorizeUrl,
            ExpiresAtUtc = handle.ExpiresAtUtc
        }, ct);
    }
}
