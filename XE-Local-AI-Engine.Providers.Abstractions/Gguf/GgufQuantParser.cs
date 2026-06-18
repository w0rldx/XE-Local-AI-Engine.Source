namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Text.RegularExpressions;

/// <summary>
///     Tolerant parser for the GGUF quant label embedded in a <c>.gguf</c> filename (e.g.
///     <c>Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf</c> → <c>Q4_K_M</c>). Shared by discovery (per-file quant) and the
///     store (selecting a file by quant). Returns <see langword="null" /> when no recognizable quant token is present so
///     callers can skip an unparseable file rather than dropping the whole repo.
/// </summary>
public static partial class GgufQuantParser
{
    // Matches the common llama.cpp quant tokens as a whole token (delimited by '-', '.', '_' or string bounds).
    // Alternatives are ordered longest-first so e.g. Q3_K_XL wins over Q3_K and Q4_0_4_4 (ARM) wins over Q4_0:
    //   K-quants: Q2_K, Q3_K_S/M/L/XL, Q4_K_S/M/L, Q5_K_S/M/L, Q6_K(_L), Q8_0
    //   legacy:   Q4_0, Q4_1, Q5_0, Q5_1 ; ARM: Q4_0_4_4/4_8/8_8
    //   IQ:       IQ1_S/M, IQ2_XXS/XS/S/M, IQ3_XXS/XS/S/M, IQ4_XS/NL
    //   floats:   F16/FP16, BF16, F32/FP32, F64
    // Verified live 2026-06-18 against bartowski/unsloth GGUF filenames (incl. multi-part -00001-of-00002 suffixes).
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(IQ[1-4]_(?:XXS|XS|S|M|NL)|Q[2-8]_K_(?:XL|S|M|L)|Q[2-8]_K|Q4_0_(?:4_4|4_8|8_8)|Q[2-8]_[01]|BF16|FP16|FP32|F16|F32|F64)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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

        return last is not null ? NormalizeCasing(last.Groups[1].Value) : null;
    }

    private static string NormalizeCasing(string token)
    {
        // Quant tokens are conventionally upper-case (Q4_K_M, IQ2_XXS, F16, BF16).
        return token.ToUpperInvariant();
    }
}
