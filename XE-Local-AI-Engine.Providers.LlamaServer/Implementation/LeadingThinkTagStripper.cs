namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>
///     Drops stray think tags and the whitespace around them from the START of a llama-server answer.
/// </summary>
/// <remarks>
///     llama-server parses reasoning onto <c>reasoning_content</c>, so a tag left at the head of <c>content</c> is
///     residue, never answer text: a <c>&lt;/think&gt;</c> after a budget-forced end (Qwen3.5 0.8B), or an empty
///     <c>&lt;think&gt;&lt;/think&gt;</c> pair a zero reasoning budget forces on a template without an enable-thinking
///     switch (LFM2.5, b10201). Only the head is touched; a tag later in the answer is left for the turn's own
///     reclassification. Buffers at most a partial tag; stateful, one instance per response.
/// </remarks>
internal sealed class LeadingThinkTagStripper
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private string _pending = string.Empty;
    private bool _done;
    private bool _strippedTag;

    /// <summary>Feeds one content chunk and returns what can be emitted now, possibly empty.</summary>
    public string Push(string text)
    {
        if (_done)
        {
            return text;
        }

        _pending += text;
        while (true)
        {
            var head = _pending.TrimStart();
            if (head.StartsWith(CloseTag, StringComparison.Ordinal))
            {
                _pending = head[CloseTag.Length..];
                _strippedTag = true;
                continue;
            }

            // An open tag counts as residue only when its close follows at once: anything else is text the parser missed.
            if (head.StartsWith(OpenTag, StringComparison.Ordinal))
            {
                var afterOpen = head[OpenTag.Length..].TrimStart();
                if (afterOpen.StartsWith(CloseTag, StringComparison.Ordinal))
                {
                    _pending = afterOpen[CloseTag.Length..];
                    _strippedTag = true;
                    continue;
                }

                if (IsPrefixOf(afterOpen, CloseTag))
                {
                    return string.Empty;
                }
            }
            else if (IsPrefixOf(head, OpenTag) || IsPrefixOf(head, CloseTag))
            {
                return string.Empty;
            }

            var emitted = DropLeadingBlankLines(_pending);
            _done = true;
            _pending = string.Empty;
            return emitted;
        }
    }

    /// <summary>Releases a held partial tag at the end of the response; whitespace-only residue is dropped.</summary>
    public string Flush()
    {
        var rest = _done || _pending.TrimStart().Length == 0 ? string.Empty : DropLeadingBlankLines(_pending);
        _done = true;
        _pending = string.Empty;
        return rest;
    }

    // Leading blank lines go, and same-line whitespace after a stripped tag; the indentation of real first-line content stays.
    private string DropLeadingBlankLines(string text)
    {
        var start = text.Length - text.TrimStart().Length;
        if (start == 0)
        {
            return text;
        }

        var lastNewline = text.LastIndexOf('\n', start - 1);
        if (lastNewline >= 0)
        {
            return text[(lastNewline + 1)..];
        }

        return _strippedTag ? text[start..] : text;
    }

    // Empty counts as a prefix: whitespace alone cannot decide anything yet.
    private static bool IsPrefixOf(string head, string tag) =>
        head.Length < tag.Length && tag.StartsWith(head, StringComparison.Ordinal);
}
