namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using System.Text;

/// <summary>Turns one exec stream's raw chunks into lines for an <see cref="ISandboxOutputObserver" />, split as the process pump splits them.</summary>
/// <remarks>
///     The UTF-8 decoder keeps a sequence cut across two chunks intact, and a <c>\r\n</c> cut the same way stays one terminator.
///     A line longer than <see cref="MaxLineChars" /> reaches the observer as its prefix, so a newline-free flood cannot grow memory.
/// </remarks>
internal sealed class SandboxOutputLineSplitter
{
    /// <summary>The longest line kept; the rest of a longer line is discarded up to its terminator.</summary>
    internal const int MaxLineChars = 64 * 1024;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly ISandboxOutputObserver _observer;
    private readonly SandboxOutputSource _source;
    private char[] _chars = [];
    private bool _afterCarriageReturn;

    public SandboxOutputLineSplitter(ISandboxOutputObserver observer, SandboxOutputSource source)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _observer = observer;
        _source = source;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        Decode(bytes, flush: false);
    }

    /// <summary>End of stream: flushes a dangling partial sequence and emits an unterminated last line.</summary>
    public void Complete()
    {
        Decode([], flush: true);
        if (_line.Length > 0)
        {
            Emit();
        }
    }

    private void Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        var needed = _decoder.GetCharCount(bytes, flush);
        if (_chars.Length < needed)
        {
            _chars = new char[needed];
        }

        var count = _decoder.GetChars(bytes, _chars, flush);
        foreach (var character in _chars.AsSpan(0, count))
        {
            Consume(character);
        }
    }

    private void Consume(char character)
    {
        switch (character)
        {
            case '\n' when _afterCarriageReturn:
                _afterCarriageReturn = false;
                break;
            case '\n':
                Emit();
                break;
            case '\r':
                Emit();
                _afterCarriageReturn = true;
                break;
            default:
                _afterCarriageReturn = false;
                if (_line.Length < MaxLineChars)
                {
                    _ = _line.Append(character);
                }

                break;
        }
    }

    private void Emit()
    {
        _observer.OnLine(_source, _line.ToString());
        _ = _line.Clear();
    }
}
