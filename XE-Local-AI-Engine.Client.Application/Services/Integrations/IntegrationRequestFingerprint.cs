namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Security.Cryptography;
using System.Text;

/// <summary>
///     The dedup fingerprint: <c>SHA-256(UTF8(principalId) ‖ 0x1E ‖ UTF8(triggerName) ‖ 0x1E ‖
///     UTF8(sessionId-or-empty) ‖ 0x1E ‖ rawBodyBytes)</c>.
///     <para>
///         Three things about it are contract, not implementation:
///     </para>
///     <list type="bullet">
///         <item>
///             The <c>0x1E</c> separators. Without them <c>("ab","c")</c> and <c>("a","bc")</c> hash identically, which
///             would let one caller's request collide with another's.
///         </item>
///         <item>
///             The first span is the PRINCIPAL, not the key prefix, so a rotated or second credential for the same
///             integrator retries its own request successfully instead of colliding with itself.
///         </item>
///         <item>
///             The body is hashed RAW. Property order, whitespace and duplicate keys are all part of the identity, so
///             a retry must resend byte-identical bytes; anything else is a 409. There is no canonicalisation code
///             anywhere in this feature, deliberately.
///         </item>
///     </list>
///     <para>
///         The request id is the LOOKUP key and never an input to the hash, and the composed seed is never hashed
///         either — <c>UntrustedContentFraming</c> mints a fresh nonce per call, so identical inputs produce a
///         different seed every time.
///     </para>
/// </summary>
public static class IntegrationRequestFingerprint
{
    /// <summary>ASCII record separator. Part of the wire contract, not a formatting choice.</summary>
    private static readonly byte[] Separator = [0x1E];

    public static byte[] Compute(Guid principalId, string triggerName, Guid? sessionId, ReadOnlySpan<byte> rawRequestBody)
    {
        ArgumentNullException.ThrowIfNull(triggerName);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(principalId.ToString("D")));
        hash.AppendData(Separator);
        hash.AppendData(Encoding.UTF8.GetBytes(triggerName));
        hash.AppendData(Separator);

        // The empty string, never Guid.Empty's digits — a caller could send those, and then a session-less request and
        // one naming the all-zero session would be indistinguishable.
        hash.AppendData(Encoding.UTF8.GetBytes(sessionId?.ToString("D") ?? string.Empty));
        hash.AppendData(Separator);
        hash.AppendData(rawRequestBody);

        return hash.GetHashAndReset();
    }
}
