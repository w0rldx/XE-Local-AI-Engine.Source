namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     Starts (or supersedes) the Entra ID device-code sign-in flow for the stored Azure Foundry connection and
///     returns the user code and verification URL, so the UI can render a copyable link. Operator-gated.
/// </summary>
/// <remarks>
///     The token exchange completes in the background; the UI polls <c>cloud-settings/entra/device-code/status</c> for
///     completion. Never returns token material.
/// </remarks>
public sealed class EntraDeviceCodeSignInEndpoint : EndpointWithoutRequest<EntraDeviceCodeSignInResponse>
{
    private readonly IEntraDeviceCodeSignInCoordinator _signInCoordinator;

    public EntraDeviceCodeSignInEndpoint(IEntraDeviceCodeSignInCoordinator signInCoordinator)
    {
        ArgumentNullException.ThrowIfNull(signInCoordinator);
        _signInCoordinator = signInCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.CloudSettings.EntraDeviceCodeStart);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Only the user-actionable "no Entra connection configured" precondition is surfaced as a 400, with its path-free message, by DomainValidationExceptionHandler.
        // Every other failure flows to the global handlers for a clean 500: catching the base InvalidOperationException here would swallow it and leak its raw message.
        var handle = await _signInCoordinator.StartAsync(ct);
        await Send.OkAsync(new EntraDeviceCodeSignInResponse
        {
            UserCode = handle.UserCode,
            VerificationUri = handle.VerificationUri,
            ExpiresAtUtc = handle.ExpiresAtUtc
        }, ct);
    }
}
