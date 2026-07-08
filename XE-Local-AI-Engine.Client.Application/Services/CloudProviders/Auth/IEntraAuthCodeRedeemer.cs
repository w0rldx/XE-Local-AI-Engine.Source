namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using Microsoft.Identity.Client;

/// <summary>Result of redeeming an authorization code via an MSAL confidential-client PKCE exchange.</summary>
public sealed record EntraAuthCodeRedemptionResult(IConfidentialClientApplication ConfidentialClientApplication, IAccount Account);

/// <summary>
///     Seam over the real MSAL authorization-code redemption call
///     (<c>ConfidentialClientApplicationBuilder</c> + <c>AcquireTokenByAuthorizationCode(...).WithPkceCodeVerifier(...).ExecuteAsync()</c>)
///     so <see cref="EntraAuthCodeSignInCoordinator" />'s unit tests can fake a successful or failed redemption
///     without a real AAD round-trip or a real client secret. MSAL's fluent request builders are sealed/internal and
///     not mockable, so this narrow interface abstracts only the redemption CALL — not the whole MSAL surface — and
///     <see cref="EntraAuthCodeRedeemer" /> is the only production caller of the actual MSAL API. Public only because
///     <see cref="EntraAuthCodeSignInCoordinator" />'s own constructor is public (mirrors
///     <see cref="EntraDeviceCodeSignInCoordinator" />'s all-public dependency list) — not part of the intended
///     public API surface otherwise.
/// </summary>
public interface IEntraAuthCodeRedeemer
{
    Task<EntraAuthCodeRedemptionResult> RedeemAsync(StoredAzureFoundryConnection connection,
        string authorizationCode,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken);
}
