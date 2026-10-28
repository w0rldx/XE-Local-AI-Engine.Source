namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

/// <summary>
///     One PKCE exchange's secret and its S256 digest: the verifier stays with the client until the token request,
///     the challenge is what the authorization request carries.
/// </summary>
internal sealed record PkcePair(string CodeVerifier, string CodeChallenge);
