namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Security.Cryptography;
using System.Text;

/// <summary>
///     The dedup fingerprint: <c>SHA-256(UTF8(principalId) ‖ 0x1E ‖ UTF8(triggerName) ‖ 0x1E ‖
///     UTF8(sessionId-or-empty) ‖ 0x1E ‖ rawBodyBytes)</c>.
/// </summary>
/// <remarks>
///     Three things are contract. The <c>0x1E</c> separators: without them <c>("ab","c")</c> and <c>("a","bc")</c> hash
///     identically, letting one caller's request collide with another's. The first span is the PRINCIPAL, not the key
///     prefix, so a rotated credential retries its own request instead of colliding with itself. The body is hashed RAW —
///     order, whitespace and duplicate keys are identity, so a retry must resend byte-identical bytes and nothing
///     canonicalises. The request id is the LOOKUP key, never an input; nor is the seed, whose fence nonce differs per call.
/// </remarks>
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
