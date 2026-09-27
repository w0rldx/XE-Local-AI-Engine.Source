namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
///     Pulls the final numeric answer out of free-form model text. The extraction ORDER is the contract — a model that
///     shows its working leaves several numbers behind, and the last one it wrote is not reliably the one it meant.
/// </summary>
public static class BenchmarkMathAnswer
{
    /// <summary>The order tried, most explicit first. Documented here because a test pins it by name.</summary>
    public const string ExtractionOrder = "boxed, hash, phrase, last-number";

    private static readonly Regex HashMarker =
        new(@"####\s*(?<value>[^\r\n]{1,64})", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static readonly Regex AnswerPhrase =
        // The captured value keeps commas: they are thousands separators as often as punctuation, and Clean strips
        // both. Excluding them here read "$1,234,567" as 1.
        new(@"(?i:answer)\s*(?i:is)?\s*[:=]?\s*(?<value>[^\s;]{1,64})",
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));

    private static readonly Regex AnyNumber =
        new(@"[-+]?[0-9][0-9,_]*(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?(?:\s*/\s*[-+]?[0-9][0-9,_]*(?:\.[0-9]+)?)?",
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));

    private const string CurrencyAndNoise = "$€£¥%";

    /// <summary>
    ///     The answer this text states, and which rule found it. Returns <see langword="false" /> when no number can
    ///     be read at all — which is a FAILED criterion, never a zero-valued pass.
    /// </summary>
    public static bool TryExtract(string? text, out double value, out string source)
    {
        value = 0;
        source = "none";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (TryLastBoxed(text, out var boxed) && TryParseNumber(boxed, out value))
        {
            source = "boxed";
            return true;
        }

        if (TryLastMatch(HashMarker, text, out var hashed) && TryParseNumber(hashed, out value))
        {
            source = "hash";
            return true;
        }

        if (TryLastMatch(AnswerPhrase, text, out var phrased) && TryParseNumber(phrased, out value))
        {
            source = "phrase";
            return true;
        }

        if (TryLastMatch(AnyNumber, text, group: null, out var trailing) && TryParseNumber(trailing, out value))
        {
            source = "last-number";
            return true;
        }

        return false;
    }

    /// <summary>
    ///     A number as a model writes one: thousands separators, a currency symbol, trailing punctuation, scientific
    ///     notation, or a plain fraction such as <c>1/2</c> — which must compare equal to <c>0.5</c>.
    /// </summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = Clean(text);
        var slash = cleaned.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            if (!TryParseScalar(cleaned[..slash], out var numerator)
                || !TryParseScalar(cleaned[(slash + 1)..], out var denominator)
                || denominator == 0)
            {
                return false;
            }

            value = numerator / denominator;
            return double.IsFinite(value);
        }

        return TryParseScalar(cleaned, out value);
    }

    private static string Clean(string text) =>
        string.Concat(text.Where(static character => character is not (',' or '_' or ' ')
                                                     && !CurrencyAndNoise.Contains(character, StringComparison.Ordinal)))
              .Trim('.', ';', ':', ')', ']', '}', '*', '"', '\'');

    private static bool TryParseScalar(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>The LAST <c>\boxed{…}</c>, brace-matched so a nested <c>\frac{a}{b}</c> is read whole.</summary>
    private static bool TryLastBoxed(string text, out string content)
    {
        content = string.Empty;
        var found = false;
        var index = 0;
        while ((index = text.IndexOf(@"\boxed{", index, StringComparison.Ordinal)) >= 0)
        {
            var start = index + 7;
            var depth = 1;
            var cursor = start;
            while (cursor < text.Length && depth > 0)
            {
                depth += text[cursor] switch
                {
                    '{' => 1,
                    '}' => -1,
                    _ => 0
                };
                cursor++;
            }

            if (depth == 0)
            {
                content = Unwrap(text[start..(cursor - 1)]);
                found = true;
            }

            index = start;
        }

        return found;
    }

    /// <summary>Reduces the LaTeX a boxed answer may wrap a number in to the number itself.</summary>
    private static string Unwrap(string boxed)
    {
        var trimmed = boxed.Trim();
        const string Frac = @"\frac{";
        if (!trimmed.StartsWith(Frac, StringComparison.Ordinal))
        {
            return trimmed.Replace(@"\!", string.Empty, StringComparison.Ordinal)
                          .Replace(@"\,", string.Empty, StringComparison.Ordinal);
        }

        var close = trimmed.IndexOf('}', Frac.Length);
        var second = close < 0 ? -1 : trimmed.IndexOf('{', close);
        var end = second < 0 ? -1 : trimmed.IndexOf('}', second);
        return close < 0 || second < 0 || end < 0
            ? trimmed
            : $"{trimmed[Frac.Length..close]}/{trimmed[(second + 1)..end]}";
    }

    private static bool TryLastMatch(Regex pattern, string text, out string content) =>
        TryLastMatch(pattern, text, "value", out content);

    private static bool TryLastMatch(Regex pattern, string text, string? group, out string content)
    {
        content = string.Empty;
        var found = false;
        foreach (var match in pattern.Matches(text).Cast<Match>())
        {
            content = group is null ? match.Value : match.Groups[group].Value;
            found = true;
        }

        return found;
    }
}
