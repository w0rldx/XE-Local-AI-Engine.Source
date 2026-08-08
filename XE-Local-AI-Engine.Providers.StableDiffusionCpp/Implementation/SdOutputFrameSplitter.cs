namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Text;

/// <summary>
///     Cuts <c>sd-server</c>'s raw output stream into frames. Separate from
///     <see cref="ImageServerProcessLauncher" /> so the exact framing sd.cpp emits can be pinned by a fixture rather
///     than only observed in production.
/// </summary>
/// <remarks>
///     <para>
///         A frame ends at LF, at CR, or at the ANSI erase-to-end-of-line sequence. The last of those is the one that
///         matters. A hexdump of a real generation against the pinned build shows the progress bar written with a
///         <em>leading</em> carriage return and each frame closed by the erase sequence:
///     </para>
///     <code>\n \r "  |===&gt;    | 1/8 - 6.34s/it" ESC[K \r "  |=====&gt;  | 2/8 - 4.97s/it" ESC[K ... \n</code>
///     <para>
///         Because the CR <em>leads</em>, a frame's text is not terminated by anything until the next frame starts. A
///         reader that splits only on CR/LF therefore surfaces every step exactly one step late, and holds the final
///         step until sampling ends entirely — a bar permanently one behind and an ETA computed from stale counters.
///         Treating the erase sequence as a terminator flushes each frame the instant it is written. All 19 frames of
///         the captured run ended with it and it was the only escape sequence anywhere in the capture.
///     </para>
///     <para>Not thread-safe: one splitter belongs to exactly one stream's drain loop.</para>
/// </remarks>
internal sealed class SdOutputFrameSplitter
{
    /// <summary>ANSI erase-to-end-of-line — the in-band frame terminator described in the remarks.</summary>
    internal const string EraseToEndOfLine = "\u001b[K";

    /// <summary>
    ///     Hard cap on one reassembled frame. sd.cpp's longest genuine line is a model path; anything past this arrived
    ///     with no terminator at all, and buffering it unbounded would trade a full child pipe for an unbounded string.
    ///     Over the cap the frame is emitted as-is and reassembly restarts.
    /// </summary>
    internal const int MaxFrameLength = 8 * 1024;

    private readonly Action<string> _onFrame;
    private readonly StringBuilder _frame = new();

    public SdOutputFrameSplitter(Action<string> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        _onFrame = onFrame;
    }

    /// <summary>Feeds one read's worth of characters, emitting every frame that completes within it.</summary>
    public void Append(ReadOnlySpan<char> chunk)
    {
        foreach (var character in chunk)
        {
            AppendCore(character);
        }
    }

    /// <summary>Emits whatever the child wrote without a trailing terminator. Called once at EOF.</summary>
    public void Flush()
    {
        EmitFrame();
    }

    private void AppendCore(char character)
    {
        if (character is '\n' or '\r')
        {
            EmitFrame();
            return;
        }

        _ = _frame.Append(character);

        if (EndsWithEraseSequence())
        {
            // The terminator itself is not part of the frame's text.
            _frame.Length -= EraseToEndOfLine.Length;
            EmitFrame();
            return;
        }

        if (_frame.Length >= MaxFrameLength)
        {
            EmitFrame();
        }
    }

    private bool EndsWithEraseSequence()
    {
        if (_frame.Length < EraseToEndOfLine.Length)
        {
            return false;
        }

        var start = _frame.Length - EraseToEndOfLine.Length;
        for (var index = 0; index < EraseToEndOfLine.Length; index++)
        {
            if (_frame[start + index] != EraseToEndOfLine[index])
            {
                return false;
            }
        }

        return true;
    }

    private void EmitFrame()
    {
        if (_frame.Length == 0)
        {
            return;
        }

        var frame = _frame.ToString();
        _frame.Clear();
        _onFrame(frame);
    }
}
