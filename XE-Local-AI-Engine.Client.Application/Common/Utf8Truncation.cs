namespace XE_Local_AI_Engine.Client.Common;

/// <summary>Cuts text to a UTF-8 BYTE budget at a whole-rune boundary.</summary>
/// <remarks>
///     A surrogate-only guard bounds nothing: a 3-byte CJK glyph is a single <see cref="char" />, so it would overshoot
///     by up to two bytes per character, and a plain <c>GetBytes</c>/<c>GetString</c> slice would split one.
/// </remarks>
public static class Utf8Truncation
{
    /// <summary>The longest prefix of <paramref name="value" /> whose UTF-8 encoding fits in <paramref name="budget" /> bytes.</summary>
    public static string TruncateToUtf8ByteBudget(string value, int budget)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (budget <= 0)
        {
            return string.Empty;
        }

        var used = 0;
        var lastCharIndex = 0;
        var charIndex = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > budget)
            {
                break;
            }

            used += rune.Utf8SequenceLength;
            charIndex += rune.Utf16SequenceLength;
            lastCharIndex = charIndex;
        }

        return value[..lastCharIndex];
    }
}
