namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deterministic, allocation-bounded extraction of one useful declaration symbol from a code chunk.
/// </summary>
/// <remarks>
///     Not an AST replacement: it supplies the weighted lexical field with common class and function identifiers while
///     preserving the raw chunk as the authoritative search content.
/// </remarks>
internal static class KnowledgeCodeSymbolExtractor
{
    private static readonly string[] DeclarationPrefixes =
    [
        "class ", "interface ", "struct ", "record ", "enum ", "def ", "func ", "function ",
        "fn ", "type ", "module ", "trait "
    ];

    // Only a last resort: a namespace is the first line of every file-scoped C# chunk, not the chunk's symbol.
    private static readonly string[] NamespacePrefixes = ["namespace "];

    private static readonly HashSet<string> ControlWords = new(StringComparer.Ordinal)
    {
        "if",
        "for",
        "foreach",
        "while",
        "switch",
        "catch",
        "return",
        "new",
        "using",
        "lock",
        "sizeof",
        "typeof",
        "nameof"
    };

    public static string? ExtractPrimary(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return FindFirstDeclaration(content) ?? FindPrefixedIdentifier(content, NamespacePrefixes);
    }

    private static string? FindFirstDeclaration(string content)
    {
        foreach (var rawLine in content.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (IsSkipped(line))
            {
                continue;
            }

            var symbol = ReadPrefixedIdentifier(line, DeclarationPrefixes);
            if (symbol is not null)
            {
                return symbol;
            }

            var openParenthesis = line.IndexOf('(');
            if (openParenthesis <= 0)
            {
                continue;
            }

            var declaration = line[..openParenthesis].TrimEnd();
            var tokenStart = declaration.LastIndexOfAny(" \t.:") + 1;
            var candidate = ReadIdentifier(declaration[tokenStart..]);
            if (candidate is not null && !ControlWords.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindPrefixedIdentifier(string content, string[] prefixes)
    {
        foreach (var rawLine in content.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            var symbol = IsSkipped(line) ? null : ReadPrefixedIdentifier(line, prefixes);
            if (symbol is not null)
            {
                return symbol;
            }
        }

        return null;
    }

    private static string? ReadPrefixedIdentifier(ReadOnlySpan<char> line, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            var prefixIndex = line.IndexOf(prefix, StringComparison.Ordinal);
            if (prefixIndex >= 0)
            {
                var symbol = ReadIdentifier(line[(prefixIndex + prefix.Length)..]);
                if (symbol is not null)
                {
                    return symbol;
                }
            }
        }

        return null;
    }

    private static bool IsSkipped(ReadOnlySpan<char> line)
    {
        return line.IsEmpty || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#');
    }

    private static string? ReadIdentifier(ReadOnlySpan<char> input)
    {
        input = input.TrimStart();
        var length = 0;
        while (length < input.Length && (char.IsLetterOrDigit(input[length]) || input[length] is '_' or '$'))
        {
            length++;
        }

        return length == 0 ? null : input[..length].ToString();
    }
}
