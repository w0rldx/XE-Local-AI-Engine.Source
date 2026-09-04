namespace XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     The KV-cache element types this application exposes for <c>-ctk</c>/<c>-ctv</c>, and the single authority for
///     whether a token is one of them.
/// </summary>
/// <remarks>
///     <para>
///         The allow-list is <c>f16 | q8_0 | q4_0</c>, symmetric by construction (K == V). <c>f16</c> emits no
///         <c>-ctk/-ctv</c> at all and leaves flash attention at the runtime's own default; a quantized type sets both
///         cache types and requires <c>-fa on</c>.
///     </para>
///     <para>
///         <strong>This type answers exactly one question — "is this token a valid KV-cache type".</strong> A blank
///         value means two different things to the two callers and neither may read the other's meaning out of
///         <see cref="TryNormalize" />: for the node setting it means "use the node default" (resolved to
///         <see cref="Q8_0" /> by the settings accessor), and for a benchmark run it means "Auto, decided at freeze".
///         Each caller resolves blank itself.
///     </para>
/// </remarks>
public static class LlamaServerKvCacheTypes
{
    /// <summary>Unquantized 16-bit KV cache: no <c>-ctk/-ctv</c> is emitted and flash attention is left at its default.</summary>
    public const string F16 = "f16";

    /// <summary>8-bit KV cache — the default. Effectively lossless quality at half the KV bytes; requires flash attention.</summary>
    public const string Q8_0 = "q8_0";

    /// <summary>4-bit KV cache: a quarter of the f16 KV bytes, traded against answer quality. Requires flash attention.</summary>
    public const string Q4_0 = "q4_0";

    private static readonly string[] SupportedTypes = [F16, Q8_0, Q4_0];

    /// <summary>
    ///     <see langword="true" /> when <paramref name="type" /> is one of the allowed types (case-insensitively), or is
    ///     <see langword="null" />/blank — blank means "absent", which every caller resolves for itself.
    /// </summary>
    public static bool IsAllowed(string? type)
    {
        return TryNormalize(type, out _);
    }

    /// <summary>
    ///     Canonicalizes a requested type: trimmed and resolved to this list's lowercase form, so operator casing is
    ///     forgiven. A missing/blank request yields <see langword="true" /> with a <see langword="null" />
    ///     <paramref name="normalized" /> ("absent"); an unrecognized value yields <see langword="false" />.
    /// </summary>
    public static bool TryNormalize(string? requested, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        var candidate = requested.Trim();
        normalized = Array.Find(SupportedTypes, supported => string.Equals(candidate, supported, StringComparison.OrdinalIgnoreCase));
        return normalized is not null;
    }

    /// <summary><see langword="true" /> when the canonical <paramref name="type" /> needs <c>-ctk/-ctv</c> + <c>-fa on</c>.</summary>
    public static bool IsQuantized(string type)
    {
        return string.Equals(type, Q8_0, StringComparison.Ordinal) || string.Equals(type, Q4_0, StringComparison.Ordinal);
    }
}
