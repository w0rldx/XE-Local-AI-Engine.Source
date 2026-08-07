namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

/// <summary>
///     Response to <c>POST cloud-settings/entra/auth-code/start</c>: the URL the UI opens in a new browser tab. The
///     exchange completes in the background once AAD redirects back to the loopback listener; the UI polls
///     <see cref="EntraAuthCodeSignInStatusResponse" />. Carries no token material.
/// </summary>
public sealed record EntraAuthCodeSignInResponse
{
    /// <summary>The URL to open in a browser to complete sign-in.</summary>
    public required string AuthorizeUrl { get; init; }

    /// <summary>When this pending attempt gives up waiting for the browser callback.</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

/// <summary>
///     Response to <c>GET cloud-settings/entra/auth-code/status</c>: the current Entra ID authorization-code
///     sign-in state. Carries no token material and no AAD error detail — only lifecycle state.
/// </summary>
public sealed record EntraAuthCodeSignInStatusResponse
{
    /// <summary>The sign-in lifecycle state, as the <c>EntraAuthCodeSignInState</c> enum name.</summary>
    public required string State { get; init; }

    /// <summary>When the pending attempt gives up waiting for the browser callback, when a sign-in is pending.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}
