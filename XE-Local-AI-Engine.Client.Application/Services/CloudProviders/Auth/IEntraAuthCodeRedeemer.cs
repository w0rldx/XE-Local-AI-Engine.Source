namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using Microsoft.Identity.Client;

/// <summary>Result of redeeming an authorization code via an MSAL confidential-client PKCE exchange.</summary>
public sealed class EntraAuthCodeRedemptionResult
{
    public required IConfidentialClientApplication ConfidentialClientApplication { get; init; }

    public required IAccount Account { get; init; }
}

/// <summary>
///     Seam over the real MSAL authorization-code redemption call, so <see cref="EntraAuthCodeSignInCoordinator" />'s
///     unit tests can fake a successful or failed redemption without a real AAD round-trip or a real client secret.
/// </summary>
/// <remarks>
///     MSAL's fluent request builders
///     (<c>ConfidentialClientApplicationBuilder</c> + <c>AcquireTokenByAuthorizationCode(...).WithPkceCodeVerifier(...).ExecuteAsync()</c>)
///     are sealed/internal and not mockable, so this abstracts only the redemption CALL, never the whole MSAL surface;
///     <see cref="EntraAuthCodeRedeemer" /> is the only production caller of the real MSAL API. Public only because
///     <see cref="EntraAuthCodeSignInCoordinator" />'s own constructor is public, not as intended public API.
/// </remarks>
public interface IEntraAuthCodeRedeemer
{
    Task<EntraAuthCodeRedemptionResult> RedeemAsync(StoredAzureFoundryConnection connection,
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken);
}
