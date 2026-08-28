namespace XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;

using Microsoft.Extensions.AI;

/// <summary>
///     Splits a LEADING <c>&lt;think&gt;…&lt;/think&gt;</c> block out of a model's text output into
///     <see cref="TextReasoningContent" />, leaving the rest as ordinary <see cref="TextContent" />.
/// </summary>
/// <remarks>
///     <para>
///         This is the LAST-RESORT branch of reasoning detection, used only when a server surfaced no reasoning channel
///         of its own. Some OpenAI-compatible servers (and many chat templates driven through them) emit the model's
///         raw thinking inline in <c>content</c> instead of a separate field. Left alone, that text is rendered to the
///         user as the answer, and it also gets replayed verbatim as conversation history on the next turn.
///     </para>
///     <para>
///         Only a LEADING block is recognised, and only one: an interior <c>&lt;think&gt;</c> is far more likely to be
///         a model quoting markup than a reasoning channel, and stripping it would silently delete part of a legitimate
///         answer. Everything after the first <c>&lt;/think&gt;</c> is passed through untouched.
///     </para>
///     <para>
///         The splitter is a streaming state machine: a tag can arrive split across SSE deltas, so text is buffered only
///         as far as it must be — at most the length of the tag being matched — and released as soon as the decision is
///         unambiguous. It is stateful and single-consumer; one instance serves one response.
///     </para>
/// </remarks>
internal sealed class ThinkTagReasoningSplitter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private readonly List<AIContent> _emitted = [];
    private string _buffer = string.Empty;
    private SplitState _state = SplitState.Undecided;

    private enum SplitState
    {
        /// <summary>Still deciding whether the output opens with a think block.</summary>
        Undecided,

        /// <summary>Inside the think block; text becomes reasoning until the close tag.</summary>
        Reasoning,

        /// <summary>Decided: everything from here on is ordinary content.</summary>
        Passthrough
    }

    /// <summary>
    ///     True once the splitter has committed to passing text straight through — either it saw the close tag or it
    ///     established that the output never opened with one. Callers use it to stop paying for the state machine.
    /// </summary>
    public bool IsPassthrough => _state == SplitState.Passthrough;

    /// <summary>
    ///     Feeds one text chunk in and returns whatever can now be emitted, in order. May return nothing when the chunk
    ///     is entirely consumed into the pending tag-match buffer.
    /// </summary>
    public IReadOnlyList<AIContent> Push(string text)
    {
        _emitted.Clear();
        if (string.IsNullOrEmpty(text))
        {
            return _emitted;
        }

        if (_state == SplitState.Passthrough)
        {
            _emitted.Add(new TextContent(text));
            return _emitted;
        }

        _buffer += text;
        if (_state == SplitState.Undecided && !TryLeaveUndecided())
        {
            return _emitted;
        }

        if (_state == SplitState.Reasoning)
        {
            DrainReasoning();
        }

        return _emitted;
    }

    /// <summary>
    ///     Releases whatever is still buffered at the end of the response. An unterminated think block is emitted as
    ///     reasoning rather than dropped — the model's output ending mid-thought is a truncation, not a reason to lose
    ///     the text.
    /// </summary>
    public IReadOnlyList<AIContent> Flush()
    {
        _emitted.Clear();
        if (_buffer.Length == 0)
        {
            return _emitted;
        }

        var remainder = _buffer;
        _buffer = string.Empty;
        _emitted.Add(_state == SplitState.Reasoning ? new TextReasoningContent(remainder) : new TextContent(remainder));
        _state = SplitState.Passthrough;
        return _emitted;
    }

    // Decides whether the buffered prefix opens a think block. Returns false while the answer is still ambiguous (the
    // buffer holds only whitespace, or a partial "<think>" that could still complete on the next chunk).
    private bool TryLeaveUndecided()
    {
        var leading = _buffer.AsSpan().TrimStart();
        if (leading.Length == 0)
        {
            return false;
        }

        if (leading.StartsWith(OpenTag, StringComparison.Ordinal))
        {
            _state = SplitState.Reasoning;
            _buffer = leading[OpenTag.Length..].ToString();
            return true;
        }

        if (leading.Length < OpenTag.Length && OpenTag.AsSpan(start: 0, leading.Length).SequenceEqual(leading))
        {
            return false;
        }

        // Not a think block: release everything buffered so far (leading whitespace included — it was the model's) and
        // never inspect this response's text again.
        _state = SplitState.Passthrough;
        _emitted.Add(new TextContent(_buffer));
        _buffer = string.Empty;
        return false;
    }

    // Emits as much buffered reasoning as is safe: everything up to the close tag once seen, otherwise everything
    // except a tail short enough to still be the start of a split close tag.
    private void DrainReasoning()
    {
        var closeIndex = _buffer.IndexOf(CloseTag, StringComparison.Ordinal);
        if (closeIndex >= 0)
        {
            if (closeIndex > 0)
            {
                _emitted.Add(new TextReasoningContent(_buffer[..closeIndex]));
            }

            var trailing = _buffer[(closeIndex + CloseTag.Length)..];
            _buffer = string.Empty;
            _state = SplitState.Passthrough;
            if (trailing.Length > 0)
            {
                _emitted.Add(new TextContent(trailing));
            }

            return;
        }

        var safeLength = _buffer.Length - (CloseTag.Length - 1);
        if (safeLength <= 0)
        {
            return;
        }

        _emitted.Add(new TextReasoningContent(_buffer[..safeLength]));
        _buffer = _buffer[safeLength..];
    }
}
