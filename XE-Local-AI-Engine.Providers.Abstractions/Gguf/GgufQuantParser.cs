namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Text.RegularExpressions;

/// <summary>
///     Tolerant parser for the GGUF quant label embedded in a <c>.gguf</c> filename (e.g.
///     <c>Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf</c> → <c>Q4_K_M</c>). Shared by discovery (per-file quant) and the
///     store (selecting a file by quant). Returns <see langword="null" /> when no recognizable quant token is present so
///     callers can skip an unparseable file rather than dropping the whole repo.
/// </summary>
/// <remarks>
///     Unsloth "Dynamic" quants carry a <c>UD-</c> marker in the filename (e.g.
///     <c>gemma-3-12b-it-UD-Q4_K_XL.gguf</c>). The marker is preserved as part of the canonical token
///     (<c>UD-Q4_K_XL</c>) so a Dynamic quant is a distinct, selectable identity rather than silently collapsing onto
///     its base token — a repo can ship both <c>Q4_K_M</c> and <c>UD-Q4_K_M</c>. Use <see cref="IsDynamic" /> /
///     <see cref="StripDynamicPrefix" /> to compare a Dynamic quant against a base quant.
/// </remarks>
public static partial class GgufQuantParser
{
    /// <summary>The canonical marker prefix for an Unsloth "Dynamic" (UD) quant (e.g. <c>UD-Q4_K_XL</c>).</summary>
    public const string DynamicPrefix = "UD-";

    // Matches the common llama.cpp quant tokens as a whole token (delimited by '-', '.', '_' or string bounds),
    // with an optional Unsloth "Dynamic" UD- (or UD_) marker captured separately (the "dynamic" group) so it can be preserved.
    // Alternatives are ordered longest-first so e.g. Q3_K_XL wins over Q3_K and Q4_0_4_4 (ARM) wins over Q4_0:
    //   K-quants: Q2_K, Q3_K_S/M/L/XL, Q4_K_S/M/L, Q5_K_S/M/L, Q6_K(_L), Q8_0
    //   legacy:   Q4_0, Q4_1, Q5_0, Q5_1 ; ARM: Q4_0_4_4/4_8/8_8
    //   IQ:       IQ1_S/M, IQ2_XXS/XS/S/M, IQ3_XXS/XS/S/M, IQ4_XS/NL
    //   floats:   F16/FP16, BF16, F32/FP32, F64
    //   native:   MXFP4, NVFP4 (models ship weights natively at these formats — recognized so they are not skipped).
    //             An UNRECOGNIZED token is not merely mis-priced: the file fails IsUsableGgufFile, so a repo whose files
    //             all use it disappears from search behind "No GGUF repositories matched that search". Verified live
    //             2026-07-31 against tngtech/Qwen3.6-27B-NVFP4-GGUF (invisible) and
    //             s-batman/Ornith-1.0-9B-NVFP4-MTP-GGUF (visible, but its NVFP4 file silently absent from the picker).
    //   Unsloth:  an optional UD- prefix on any of the above (UD-Q4_K_XL, UD-IQ2_M, ...)
    // Verified live 2026-06-18 against bartowski/unsloth GGUF filenames (incl. multi-part -00001-of-00002 suffixes).
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?<dynamic>UD[-_])?(?<quant>IQ[1-4]_(?:XXS|XS|S|M|NL)|Q[2-8]_K_(?:XL|S|M|L)|Q[2-8]_K|Q4_0_(?:4_4|4_8|8_8)|Q[2-8]_[01]|MXFP4|NVFP4|BF16|FP16|FP32|F16|F32|F64)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex QuantRegex();

    /// <summary>Extracts the canonical quant token from <paramref name="fileName" />, or <see langword="null" /> if none.</summary>
    public static string? TryParse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Take the LAST match: a quant token sits near the end, before any sharded -00001-of-00002 / .gguf suffix.
        Match? last = null;
        for (var match = QuantRegex().Match(fileName); match.Success; match = match.NextMatch())
        {
            last = match;
        }

        if (last is null)
        {
            return null;
        }

        // The "quant" group is the base quant token; the "dynamic" group is the optional Unsloth Dynamic marker —
        // preserve it so the Dynamic quant stays a distinct identity (UD-Q4_K_XL), normalized to the canonical UD- form.
        var quant = NormalizeCasing(last.Groups["quant"].Value);
        return last.Groups["dynamic"].Success ? DynamicPrefix + quant : quant;
    }

    /// <summary>Whether <paramref name="quant" /> carries the Unsloth Dynamic (UD) marker (e.g. <c>UD-Q4_K_XL</c>).</summary>
    public static bool IsDynamic(string quant)
    {
        ArgumentNullException.ThrowIfNull(quant);
        return quant.StartsWith("UD-", StringComparison.OrdinalIgnoreCase)
               || quant.StartsWith("UD_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the base quant with any Unsloth Dynamic (UD) marker removed (e.g. <c>UD-Q4_K_XL</c> → <c>Q4_K_XL</c>).</summary>
    public static string StripDynamicPrefix(string quant)
    {
        ArgumentNullException.ThrowIfNull(quant);
        return IsDynamic(quant) ? quant[3..] : quant;
    }

    private static string NormalizeCasing(string token)
    {
        // Quant tokens are conventionally upper-case (Q4_K_M, IQ2_XXS, F16, BF16).
        return token.ToUpperInvariant();
    }
}
