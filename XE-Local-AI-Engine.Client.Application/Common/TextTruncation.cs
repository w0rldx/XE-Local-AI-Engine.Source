namespace XE_Local_AI_Engine.Client.Common;

/// <summary>Cuts text to a <see cref="char" /> budget without splitting a surrogate pair.</summary>
public static class TextTruncation
{
    /// <summary>The longest prefix of at most <paramref name="maximumChars" /> chars that ends on a whole rune.</summary>
    public static string TruncateAtRuneBoundary(string value, int maximumChars)
    {
        if (value.Length <= maximumChars)
        {
            return value;
        }

        return value[..MoveBeforeSplitSurrogate(value, maximumChars)];
    }

    /// <summary><paramref name="index" />, or one less when a cut there would split a surrogate pair.</summary>
    public static int MoveBeforeSplitSurrogate(string value, int index)
    {
        return index > 0
               && index < value.Length
               && char.IsHighSurrogate(value[index - 1])
               && char.IsLowSurrogate(value[index])
            ? index - 1
            : index;
    }
}
