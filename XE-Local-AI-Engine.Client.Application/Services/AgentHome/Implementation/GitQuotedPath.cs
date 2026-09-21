namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text;

/// <summary>
///     Decodes one C-quoted patch path — the inverse of git's <c>quote_c_style</c> — into the name git itself acts
///     on, or refuses it.
/// </summary>
/// <remarks>
///     Never more permissive than git's own <c>unquote_c_style</c>. git answers a literal it cannot read by taking
///     the raw text, quotes included, as the name, so decoding something git rejects would validate a path git never
///     writes. Octal is therefore exactly three digits and at most one byte, and the decoded bytes must be UTF-8.
/// </remarks>
internal static class GitQuotedPath
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Whether a raw patch path is C-quoted. git quotes on a quote, a backslash or a control byte.</summary>
    public static bool IsQuoted(string path)
    {
        return path.StartsWith('"');
    }

    /// <summary>The index of the literal's closing quote, or <c>-1</c> when it has none.</summary>
    public static int FindClosingQuote(string path)
    {
        var index = 1;
        while (index < path.Length)
        {
            if (path[index] == '"')
            {
                return index;
            }

            // A backslash spends the character after it, so the quote that closes the literal is the first one no
            // escape has already consumed.
            index += path[index] == '\\' ? 2 : 1;
        }

        return -1;
    }

    /// <summary>
    ///     The decoded path, or <see langword="null" /> when the literal is not one git would read: no closing quote
    ///     at the very end, an unknown or dangling escape, an octal escape over one byte, or invalid UTF-8.
    /// </summary>
    public static string? TryDecode(string path)
    {
        if (path.Length < 2 || path[0] != '"')
        {
            return null;
        }

        var bytes = new List<byte>(path.Length);
        var index = 1;
        while (index < path.Length)
        {
            if (path[index] == '"')
            {
                // Text after the closing quote makes the literal unreadable to git, which then falls back to the raw
                // bytes; refusing is the only answer that cannot disagree with the name git ends up writing.
                return index == path.Length - 1 ? TryDecodeUtf8(bytes) : null;
            }

            var appended = path[index] == '\\' ? AppendEscape(bytes, path, ref index) : AppendLiteral(bytes, path, ref index);
            if (!appended)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Appends the UTF-8 bytes of one unescaped code point, which <c>core.quotePath=false</c> leaves raw.</summary>
    private static bool AppendLiteral(List<byte> bytes, string path, ref int index)
    {
        if (!Rune.TryGetRuneAt(path, index, out var rune))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[4];
        var written = rune.EncodeToUtf8(buffer);
        bytes.AddRange(buffer[..written]);
        index += rune.Utf16SequenceLength;
        return true;
    }

    /// <summary>Appends the one byte a backslash escape stands for, or refuses an escape git does not define.</summary>
    private static bool AppendEscape(List<byte> bytes, string path, ref int index)
    {
        index++;
        if (index >= path.Length)
        {
            return false;
        }

        var escape = path[index];
        if (escape is >= '0' and <= '7')
        {
            return AppendOctal(bytes, path, ref index);
        }

        var mapped = escape switch
        {
            'a' => 0x07,
            'b' => 0x08,
            'f' => 0x0C,
            'n' => 0x0A,
            'r' => 0x0D,
            't' => 0x09,
            'v' => 0x0B,
            '"' => 0x22,
            '\\' => 0x5C,
            _ => -1
        };

        if (mapped < 0)
        {
            return false;
        }

        bytes.Add((byte)mapped);
        index++;
        return true;
    }

    /// <summary>Appends the byte an octal escape names. git writes and reads exactly three digits, never fewer.</summary>
    private static bool AppendOctal(List<byte> bytes, string path, ref int index)
    {
        if (index + 2 >= path.Length)
        {
            return false;
        }

        var value = 0;
        for (var digit = 0; digit < 3; digit++)
        {
            var character = path[index + digit];
            if (character is < '0' or > '7')
            {
                return false;
            }

            value = (value << 3) + (character - '0');
        }

        if (value > byte.MaxValue)
        {
            return false;
        }

        bytes.Add((byte)value);
        index += 3;
        return true;
    }

    private static string? TryDecodeUtf8(List<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString([.. bytes]);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
