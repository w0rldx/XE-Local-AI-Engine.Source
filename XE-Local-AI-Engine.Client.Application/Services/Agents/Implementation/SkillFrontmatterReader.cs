namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

/// <summary>
///     Splits a <c>SKILL.md</c> into its YAML frontmatter and its body.
/// </summary>
/// <remarks>
///     <para>
///         This is deliberately <em>not</em> a YAML parser. A general YAML implementation on untrusted input is its own
///         attack surface — anchors and aliases give an attacker cheap amplification, custom tags give type coercion,
///         and merge keys give aliasing — and none of it is needed here: the specification defines six known keys whose
///         values are scalars, a flat string sequence, or a flat string map. Anything structurally richer is refused
///         rather than interpreted, and unknown keys are ignored.
///     </para>
///     <para>
///         Values that would open those doors (<c>&amp;anchor</c>, <c>*alias</c>, <c>!tag</c>) are rejected outright
///         instead of being treated as text, so no reader downstream can be surprised by a value that "looks parsed".
///     </para>
/// </remarks>
internal static class SkillFrontmatterReader
{
    private const string Fence = "---";

    /// <summary>
    ///     Parses <paramref name="document" />. Returns <c>false</c> with an operator-safe
    ///     <paramref name="error" /> when the frontmatter block is missing, unterminated, or structurally unsupported.
    /// </summary>
    public static bool TryRead(string? document, out SkillFrontmatterDocument? result, out string? error)
    {
        result = null;
        error = null;

        var lines = (document ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
                                              .Replace(oldChar: '\r', newChar: '\n')
                                              .TrimStart('\uFEFF')
                                              .Split('\n');

        var open = Array.FindIndex(lines, static line => line.Trim().Length > 0);
        if (open < 0 || lines[open].Trim() != Fence)
        {
            error = "The skill must begin with a '---' YAML frontmatter fence.";
            return false;
        }

        var close = Array.FindIndex(lines, open + 1, static line => line.Trim() is Fence or "...");
        if (close < 0)
        {
            error = "The YAML frontmatter block is never closed by a '---' fence.";
            return false;
        }

        var scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryReadBlock(lines, open + 1, close, scalars, metadata, out error))
        {
            return false;
        }

        result = new SkillFrontmatterDocument(Value(scalars, "name"),
            Value(scalars, "description"),
            Value(scalars, "license"),
            Value(scalars, "compatibility"),
            Value(scalars, "allowed-tools"),
            metadata.Count == 0 ? null : metadata,
            string.Join('\n', lines[(close + 1)..]).Trim());

        return true;
    }

    private static bool TryReadBlock(string[] lines,
        int start,
        int end,
        Dictionary<string, string> scalars,
        Dictionary<string, string> metadata,
        out string? error)
    {
        error = null;
        var index = start;
        while (index < end)
        {
            var line = lines[index];
            index++;

            // Blank lines, comments, and any stray indented line the key handlers below did not consume.
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#') || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                error = "The YAML frontmatter contains a line that is not a 'key: value' pair.";
                return false;
            }

            var key = line[..colon].Trim();
            var raw = line[(colon + 1)..].Trim();

            if (raw.Length > 0 && raw[0] is '&' or '*' or '!')
            {
                error = "YAML anchors, aliases and tags are not supported in a skill frontmatter.";
                return false;
            }

            var block = TakeIndentedBlock(lines, ref index, end);
            scalars[key] = ReadValue(key, raw, block, metadata);
        }

        return true;
    }

    /// <summary>Consumes the indented (or blank) lines that continue the key just read, advancing <paramref name="index" />.</summary>
    private static List<string> TakeIndentedBlock(string[] lines, ref int index, int end)
    {
        var block = new List<string>();
        while (index < end && (lines[index].Trim().Length == 0 || char.IsWhiteSpace(lines[index][0])))
        {
            block.Add(lines[index]);
            index++;
        }

        // A trailing blank run belongs to whatever comes next, not to this value.
        while (block.Count > 0 && block[^1].Trim().Length == 0)
        {
            block.RemoveAt(block.Count - 1);
        }

        return block;
    }

    private static string ReadValue(string key, string raw, List<string> block, Dictionary<string, string> metadata)
    {
        // Block scalars ('|' literal, '>' folded, with optional chomping indicator). Real skills use these for long
        // descriptions, so refusing them would reject valid content.
        if (raw.Length is > 0 and <= 2 && raw[0] is '|' or '>')
        {
            return string.Join(raw[0] == '>' ? ' ' : '\n', block.Select(static line => line.Trim())).Trim();
        }

        if (raw.Length == 0)
        {
            if (key.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            {
                ReadMapping(block, metadata);
                return string.Empty;
            }

            // Every other block-valued key we care about is a string sequence (`allowed-tools`), which normalises to
            // the space-delimited form the specification and MAF both consume.
            return string.Join(' ', block.Select(static line => line.Trim())
                                         .Where(static line => line.StartsWith("- ", StringComparison.Ordinal) || line == "-")
                                         .Select(static line => Unquote(line[1..].Trim())));
        }

        // Flow sequence: allowed-tools: [Read, Write] — also normalised to the space-delimited form.
        if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']')
        {
            return string.Join(' ', raw[1..^1].Split(',').Select(static item => Unquote(item.Trim())).Where(static item => item.Length > 0));
        }

        return Unquote(raw);
    }

    private static void ReadMapping(List<string> block, Dictionary<string, string> metadata)
    {
        foreach (var line in block.Select(static line => line.Trim()))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && !line.StartsWith('#'))
            {
                metadata[Unquote(line[..colon].Trim())] = Unquote(line[(colon + 1)..].Trim());
            }
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1]
                   .Replace("\\n", "\n", StringComparison.Ordinal)
                   .Replace("\\t", "\t", StringComparison.Ordinal)
                   .Replace("\\\"", "\"", StringComparison.Ordinal)
                   .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return value;
    }

    private static string? Value(Dictionary<string, string> scalars, string key)
    {
        return scalars.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
    }
}

/// <summary>
///     The six specification frontmatter keys plus the body. <paramref name="AllowedTools" /> is normalised here, once,
///     to the space-delimited string form — the shape MAF consumes and the shape persistence stores — so no caller
///     downstream has to know the frontmatter could also have written it as a sequence.
/// </summary>
internal sealed record SkillFrontmatterDocument(
    string? Name,
    string? Description,
    string? License,
    string? Compatibility,
    string? AllowedTools,
    IReadOnlyDictionary<string, string>? Metadata,
    string Body);
