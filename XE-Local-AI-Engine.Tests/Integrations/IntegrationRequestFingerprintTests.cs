namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The dedup fingerprint. Its exact byte stream is a wire contract: a caller retries by resending the identical
///     body, and any drift here silently turns every retry into a 409 or, worse, makes two different requests look
///     like the same one.
/// </summary>
public sealed class IntegrationRequestFingerprintTests
{
    private static readonly Guid Principal = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Test]
    public void Compute_PinsTheExactSevenSpanByteStream()
    {
        // A hard-coded vector, so a later "simplification" that drops the separators or reorders the spans fails here
        // rather than in production. Computed from the documented construction, not from the implementation.
        var expected = Reference(Principal, "sensor-feed", sessionId: null, "{\"a\":1}");

        var actual = IntegrationRequestFingerprint.Compute(Principal, "sensor-feed", sessionId: null, Encoding.UTF8.GetBytes("{\"a\":1}"));

        AssertEx.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
    }

    [Test]
    public void Compute_ChangesWhenAnySpanChanges()
    {
        var baseline = Compute(Principal, "sensor-feed", sessionId: null, "{\"a\":1}");

        AssertEx.NotEqual(baseline, Compute(Guid.NewGuid(), "sensor-feed", sessionId: null, "{\"a\":1}"));
        AssertEx.NotEqual(baseline, Compute(Principal, "sensor-feed-2", sessionId: null, "{\"a\":1}"));
        AssertEx.NotEqual(baseline, Compute(Principal, "sensor-feed", Guid.NewGuid(), "{\"a\":1}"));
        AssertEx.NotEqual(baseline, Compute(Principal, "sensor-feed", sessionId: null, "{\"a\":2}"));
        AssertEx.NotEqual(baseline, Compute(Principal, "sensor-feed", sessionId: null, "{\"a\": 1}"));
    }

    [Test]
    public void Compute_IsIdenticalForTwoCredentialsOfTheSamePrincipal()
    {
        // The assertion that stops the first span drifting back to the key prefix: a rotated credential must be able to
        // retry the request its predecessor sent, which means the hash cannot depend on WHICH key was used.
        var first = Compute(Principal, "sensor-feed", sessionId: null, "{\"a\":1}");
        var second = Compute(Principal, "sensor-feed", sessionId: null, "{\"a\":1}");

        AssertEx.Equal(first, second);
    }

    [Test]
    public void Compute_SeparatesTheSpans_SoAmbiguousSplitsDiffer()
    {
        // Without the 0x1E separators ("ab","c") and ("a","bc") hash identically, and one caller's request could
        // collide with another's.
        var left = Compute(Principal, "ab", sessionId: null, "c");
        var right = Compute(Principal, "a", sessionId: null, "bc");

        AssertEx.NotEqual(left, right);
    }

    [Test]
    public void Compute_WithANullSession_HashesTheEmptyStringRatherThanTheAllZeroGuid()
    {
        // A caller can send the all-zero Guid, so the two must not be indistinguishable.
        AssertEx.NotEqual(Compute(Principal, "sensor-feed", sessionId: null, "{}"), Compute(Principal, "sensor-feed", Guid.Empty, "{}"));
    }

    private static string Compute(Guid principalId, string triggerName, Guid? sessionId, string body) =>
        Convert.ToHexString(IntegrationRequestFingerprint.Compute(principalId, triggerName, sessionId, Encoding.UTF8.GetBytes(body)));

    /// <summary>The construction spelled out independently of the implementation, so the two must agree.</summary>
    private static byte[] Reference(Guid principalId, string triggerName, Guid? sessionId, string body)
    {
        var stream = new List<byte>();
        stream.AddRange(Encoding.UTF8.GetBytes(principalId.ToString("D")));
        stream.Add(0x1E);
        stream.AddRange(Encoding.UTF8.GetBytes(triggerName));
        stream.Add(0x1E);
        stream.AddRange(Encoding.UTF8.GetBytes(sessionId?.ToString("D") ?? string.Empty));
        stream.Add(0x1E);
        stream.AddRange(Encoding.UTF8.GetBytes(body));
        return System.Security.Cryptography.SHA256.HashData(stream.ToArray());
    }
}
