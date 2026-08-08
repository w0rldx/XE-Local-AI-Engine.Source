namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Classifies a GGUF repo's publisher as a "trusted" packager (a known, reputable GGUF distributor or first-party
///     model org) versus an untrusted/community publisher. This is a <strong>soft signal only</strong>: it NEVER excludes
///     a repo from search — every public GGUF repo stays discoverable — it only lets the advisor gently prefer trusted
///     publishers when ranking recommendations and lets the UI badge an untrusted publisher with a
///     "review before downloading" warning. Membership only ever RAISES trust; a missing publisher is "untrusted", never
///     "blocked".
/// </summary>
public static class GgufPublisherTrust
{
    // Reputable GGUF packagers + first-party model orgs, matched case-insensitively against the repo author (the
    // segment before the first '/'). Intentionally a curated, conservative set — this is a quality nudge, not an
    // allowlist gate.
    private static readonly HashSet<string> TrustedAuthors = new(StringComparer.OrdinalIgnoreCase)
    {
        "unsloth",
        "bartowski",
        "ggml-org",
        "lmstudio-community",
        "Qwen",
        "google",
        "microsoft",
        "mistralai",
        "meta-llama",
        "deepseek-ai",
        "NousResearch",
        "MaziyarPanahi"
    };

    /// <summary>
    ///     The author segment (the text before the first <c>/</c>) of a Hugging Face repo id or canonical model name
    ///     (<c>org/name</c> or <c>org/name:quant</c>); returns the whole trimmed string when no <c>/</c> is present.
    /// </summary>
    public static string AuthorOf(string repoIdOrModelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoIdOrModelName);

        var trimmed = repoIdOrModelName.Trim();
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? trimmed[..slash] : trimmed;
    }

    /// <summary>
    ///     <see langword="true" /> when the repo's publisher is a known reputable GGUF packager / first-party model org.
    ///     Accepts a repo id (<c>org/name</c>) or a canonical model name (<c>org/name:quant</c>). A null/blank value is
    ///     untrusted, never throwing.
    /// </summary>
    public static bool IsTrustedPublisher(string? repoIdOrModelName)
    {
        return !string.IsNullOrWhiteSpace(repoIdOrModelName) && TrustedAuthors.Contains(AuthorOf(repoIdOrModelName));
    }
}
