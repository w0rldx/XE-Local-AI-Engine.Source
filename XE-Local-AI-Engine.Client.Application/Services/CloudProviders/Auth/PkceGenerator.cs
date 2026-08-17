namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using System.Security.Cryptography;
using System.Text;

/// <summary>
///     RFC 7636 PKCE code-verifier / code-challenge pair generation (S256) for the authorization-code sign-in flow.
/// </summary>
internal static class PkceGenerator
{
    // 32 random bytes base64url-encode to 43 characters — within RFC 7636's required 43-128 character verifier length.
    private const int VerifierByteLength = 32;

    public static PkcePair Create()
    {
        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(VerifierByteLength));
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return new PkcePair(codeVerifier, codeChallenge);
    }

    /// <summary>A cryptographically random opaque token for the OAuth2 <c>state</c> parameter (CSRF protection).</summary>
    public static string CreateState()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(VerifierByteLength));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
                      .TrimEnd('=')
                      .Replace('+', '-')
                      .Replace('/', '_');
    }
}

/// <summary>
///     One PKCE exchange's secret and its S256 digest: the verifier stays with the client until the token request,
///     the challenge is what the authorization request carries.
/// </summary>
internal sealed record PkcePair(string CodeVerifier, string CodeChallenge);
