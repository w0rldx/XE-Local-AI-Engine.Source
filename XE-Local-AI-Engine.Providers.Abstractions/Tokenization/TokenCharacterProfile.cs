namespace XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Script-category counts cached independently of a model divisor. This lets a later calibration affect an already
///     memoized message while keeping CJK/emoji at approximately one token per code unit and accented text at the same
///     relative half-token bias as the chars/4 fallback.
/// </summary>
public sealed class TokenCharacterProfile
{
    private int _ascii;
    private int _cjkOrEmoji;
    private int _otherNonAscii;

    public void Add(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var character in value)
        {
            if (character < 128)
            {
                _ascii++;
            }
            else if (IsCjkOrEmoji(character))
            {
                _cjkOrEmoji++;
            }
            else
            {
                _otherNonAscii++;
            }
        }
    }

    public void Add(TokenCharacterProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _ascii += other._ascii;
        _cjkOrEmoji += other._cjkOrEmoji;
        _otherNonAscii += other._otherNonAscii;
    }

    public int WeightedLength(int charsPerToken)
    {
        var divisor = Math.Clamp(charsPerToken,
            TokenEstimatorCalibrationStore.MinimumCharsPerToken,
            TokenEstimatorCalibrationStore.MaximumCharsPerToken);
        var nonAsciiWeight = Math.Max(2, (divisor + 1) / 2);
        return _ascii + (_otherNonAscii * nonAsciiWeight) + (_cjkOrEmoji * divisor);
    }

    private static bool IsCjkOrEmoji(char character)
    {
        return character is (>= '\u2E80' and <= '\u9FFF')
            or (>= '\uAC00' and <= '\uD7A3')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\uFF00' and <= '\uFFEF')
            or (>= '\uD800' and <= '\uDFFF');
    }
}
