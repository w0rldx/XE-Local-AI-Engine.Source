namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

/// <summary>
///     A bounded, sanitized ring of the last few lines the child wrote to <c>stderr</c>, so a runtime that died
///     can be reported with its own words instead of a fixed sentence.
/// </summary>
/// <remarks>
///     Bounded twice (line count and total characters) because a wedged server can write without limit. The launcher
///     feeds it stderr only, and each supervisor decides where the tail may surface (the image runtime puts it in the
///     born-dead error, the transcription runtime only in its Warning log line). Absolute paths are reduced to their
///     file name on the way in, so the tail carries no directory layout wherever it later travels.
/// </remarks>
public sealed class ProcessStderrTail
{
    private const int DefaultMaxCharacters = 4096;
    private const int DefaultMaxLines = 20;

    private readonly Lock _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly int _maxCharacters;
    private readonly int _maxLines;
    private int _characters;

    /// <summary>A crash-report tail: the last 20 lines, at most 4096 characters.</summary>
    public ProcessStderrTail()
        : this(DefaultMaxLines, DefaultMaxCharacters)
    {
    }

    /// <summary>A tail bounded by <paramref name="maxLines" /> lines and <paramref name="maxCharacters" /> characters.</summary>
    public ProcessStderrTail(int maxLines, int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        _maxLines = maxLines;
        _maxCharacters = maxCharacters;
    }

    /// <summary>Appends one sanitized line, evicting the oldest lines until both bounds hold again.</summary>
    public void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var sanitized = AbsolutePathSanitizer.Sanitize(line.Trim());
        lock (_gate)
        {
            _lines.Enqueue(sanitized);
            _characters += sanitized.Length;
            while (_lines.Count > _maxLines || (_characters > _maxCharacters && _lines.Count > 1))
            {
                _characters -= _lines.Dequeue().Length;
            }
        }
    }

    /// <summary>The retained lines joined by <c>" | "</c>, or <see langword="null" /> when the child wrote nothing.</summary>
    public string? Snapshot()
    {
        lock (_gate)
        {
            return _lines.Count == 0 ? null : string.Join(" | ", _lines);
        }
    }

    /// <summary>A copy of the retained lines, oldest first.</summary>
    public IReadOnlyList<string> Lines()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
