namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;

/// <summary>The shape of a bridge token: <c>&lt;instance id, "N" format&gt;.&lt;base64url of 32 CSPRNG bytes&gt;</c>.</summary>
/// <remarks>
///     The instance id travels in the clear, in front, so verification is a keyed row read rather than a scan of
///     every installed application's secret. That is not a weakening: the id is not the credential, the 256 bits
///     behind the separator are, and a scan would compare the presented secret against rows it was never meant for.
///     Base64url — no padding, no <c>+</c>, <c>/</c> or <c>=</c> — so the token survives an environment variable, a
///     container's own config file and an HTTP header untouched; all three are on the path to the application.
/// </remarks>
public static class ContainerBridgeToken
{
    /// <summary>Splits the instance id from the secret. A character base64url never produces, so the split is unambiguous.</summary>
    internal const char Separator = '.';

    /// <summary>256 bits, the same strength as the node's other generated secrets.</summary>
    private const int SecretByteLength = 32;

    /// <summary>Mints a token for one instance. The only place a bridge secret comes into existence.</summary>
    public static string Mint(Guid instanceId)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{instanceId:N}{Separator}{Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretByteLength))}");
    }

    /// <summary>
    ///     Splits a presented token into the instance it claims and the secret it offers. <see langword="true" /> means
    ///     the token is well formed, never that it is valid — the secret is still unverified, and the claimed instance
    ///     may not exist.
    /// </summary>
    public static bool TryParse(string? token, out Guid instanceId, out string secret)
    {
        instanceId = Guid.Empty;
        secret = string.Empty;

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var separator = token.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0 || separator == token.Length - 1)
        {
            return false;
        }

        if (!Guid.TryParseExact(token.AsSpan(0, separator), "N", out instanceId))
        {
            instanceId = Guid.Empty;
            return false;
        }

        secret = token[(separator + 1)..];
        return true;
    }
}
