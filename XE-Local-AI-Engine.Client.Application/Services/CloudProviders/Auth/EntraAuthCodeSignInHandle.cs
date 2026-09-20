namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     The immediate result of starting an Entra ID authorization-code sign-in: the URL the UI opens in a new
///     browser tab. Contains no secrets.
/// </summary>
/// <remarks>
///     The token exchange completes in the background once the browser redirects back to the loopback listener;
///     observe it via <see cref="IEntraAuthCodeSignInCoordinator.GetStatus" />.
/// </remarks>
public sealed class EntraAuthCodeSignInHandle
{
    public required string AuthorizeUrl { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }
}
