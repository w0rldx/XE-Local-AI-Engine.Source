namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     The immediate result of starting an Entra ID authorization-code sign-in: the URL the UI opens in a new
///     browser tab. The token exchange completes in the background once the browser redirects back to the loopback
///     listener; observe via <see cref="IEntraAuthCodeSignInCoordinator.GetStatus" />. Contains no secrets.
/// </summary>
public sealed record EntraAuthCodeSignInHandle(string AuthorizeUrl, DateTimeOffset ExpiresAtUtc);
