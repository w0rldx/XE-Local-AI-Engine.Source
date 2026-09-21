namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Recognizes a speculative-decoding DRAFT GGUF (an MTP "multi-token prediction" drafter) and gives it a distinct
///     quant identity so it never masquerades as a base-model quant.
/// </summary>
/// <remarks>
///     A publisher ships drafters alongside the real weights: <c>unsloth/gemma-4-12b-it-GGUF</c> carries
///     <c>MTP/mtp-gemma-4-12b-it-Q8_0.gguf</c> (0.4 GB) next to the root <c>gemma-4-12b-it-Q8_0.gguf</c> (11.8 GB), and both
///     parse to the quant token <c>Q8_0</c>. Unmarked, a flat quant list carries that label twice — the 0.4 GB drafter first,
///     because the list sorts ascending by size, graded "Highest quality" — and downloading it yields a file that is no chat
///     model at all, whose <c>{repoId}:{quant}</c> registry key collides with the real <c>Q8_0</c>'s identity.
/// </remarks>
public static class GgufDraftModel
{
    /// <summary>The quant-token marker that identifies a draft variant (e.g. <c>MTP-Q8_0</c>).</summary>
    public const string QuantPrefix = "MTP-";

    // The publisher convention for the drafter subdirectory (unsloth ships "MTP/<file>.gguf").
    private const string DraftDirectoryName = "MTP";

    // Leaf-name prefixes that mark the file itself as a drafter ("mtp-gemma-4-12b-it-Q8_0.gguf").
    private static readonly string[] DraftFileNamePrefixes = ["mtp-", "mtp_"];

    /// <summary>
    ///     <see langword="true" /> when <paramref name="fileName" /> (a repo-relative path or a bare file name) is a
    ///     speculative-decoding draft GGUF: it sits under an <c>MTP/</c> directory, or its file name starts with
    ///     <c>mtp-</c>/<c>mtp_</c>. Both separators are accepted; the match is case-insensitive.
    /// </summary>
    /// <remarks>
    ///     Deliberately narrow so a base model that merely CARRIES MTP layers is never misread as a drafter:
    ///     <c>Qwen3.6-27B-MTP-Q6_K.gguf</c> (from <c>unsloth/Qwen3.6-27B-MTP-GGUF</c>, a real 21 GB chat model) has
    ///     neither an <c>MTP/</c> segment nor an <c>mtp-</c> prefix and stays a base quant.
    /// </remarks>
    public static bool IsDraftFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var segments = fileName.Replace(oldChar: '\\', newChar: '/').Split('/');

        // Every segment but the last is a directory: an "MTP" folder is the publisher's drafter directory.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], DraftDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var leaf = segments[^1];
        return DraftFileNamePrefixes.Any(prefix => leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Marks <paramref name="quant" /> as a draft identity (<c>Q8_0</c> → <c>MTP-Q8_0</c>). Idempotent — an
    ///     already-marked token is returned unchanged, so re-running discovery never stacks prefixes.
    /// </summary>
    /// <remarks>
    ///     The fix for a drafter sharing a base model's quant token is an identity, not a filter: marking at discovery
    ///     makes the label unambiguous, keeps the registry key distinct, and gives every downstream consumer (picker,
    ///     advisor, model list, node settings) a cheap <see cref="IsDraftQuant" /> test. Drafts stay downloadable — the
    ///     app's <c>draft-*</c> speculative-decoding modes need them.
    /// </remarks>
    public static string MarkQuant(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        return IsDraftQuant(quant) ? quant : QuantPrefix + quant;
    }

    /// <summary><see langword="true" /> when <paramref name="quant" /> carries the draft marker (e.g. <c>MTP-Q8_0</c>).</summary>
    public static bool IsDraftQuant(string? quant)
    {
        return !string.IsNullOrWhiteSpace(quant)
               && quant.Trim().StartsWith(QuantPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the base quant with the draft marker removed (<c>MTP-Q8_0</c> → <c>Q8_0</c>).</summary>
    public static string StripQuantPrefix(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        var trimmed = quant.Trim();
        return IsDraftQuant(trimmed) ? trimmed[QuantPrefix.Length..] : trimmed;
    }

    /// <summary>
    ///     <see langword="true" /> when a registry model NAME (<c>{repoId}:{quant}</c>, the key
    ///     <see cref="GgufModelName.Format" /> builds) names a draft — i.e. its quant segment carries the marker.
    /// </summary>
    /// <remarks>
    ///     Used where only a name is available (an installed-model descriptor carries no file path) to keep a drafter out
    ///     of the chat surfaces while still offering it as a speculative-decoding draft.
    /// </remarks>
    public static bool IsDraftModelName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        var separatorIndex = modelName.LastIndexOf(':');
        return separatorIndex > 0
               && separatorIndex < modelName.Length - 1
               && IsDraftQuant(modelName[(separatorIndex + 1)..]);
    }
}
