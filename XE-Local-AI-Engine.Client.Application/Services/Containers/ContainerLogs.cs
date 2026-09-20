namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>How much of a container's log to read.</summary>
/// <remarks>
///     Both ceilings are clamped on the way in rather than checked at the wire. A log read is the one runtime call
///     whose cost is set by the container rather than the engine — an application logging for a week can produce
///     gigabytes — so an unbounded request would run the node out of memory through a surface that looks like a
///     diagnostic.
/// </remarks>
public sealed record ContainerLogRequest
{
    /// <summary>The smallest number of lines a request can ask for.</summary>
    public const int MinimumTailLines = 1;

    /// <summary>The largest number of lines a request can ask for.</summary>
    public const int MaximumTailLines = 2000;

    /// <summary>The smallest byte ceiling a request can ask for.</summary>
    public const int MinimumBytes = 1;

    /// <summary>The largest byte ceiling a request can ask for: 256 KiB.</summary>
    public const int MaximumBytes = 256 * 1024;

    private readonly int _tailLines = MaximumTailLines;
    private readonly int _maxBytes = MaximumBytes;

    /// <summary>Lines to read from the end of the log, clamped to <see cref="MinimumTailLines" />..<see cref="MaximumTailLines" />.</summary>
    public int TailLines
    {
        get => _tailLines;
        init => _tailLines = Math.Clamp(value, MinimumTailLines, MaximumTailLines);
    }

    /// <summary>Byte ceiling for the returned text, clamped to <see cref="MinimumBytes" />..<see cref="MaximumBytes" />.</summary>
    public int MaxBytes
    {
        get => _maxBytes;
        init => _maxBytes = Math.Clamp(value, MinimumBytes, MaximumBytes);
    }

    /// <summary>Only return entries at or after this instant, or null for no lower bound.</summary>
    public DateTimeOffset? SinceUtc { get; init; }
}

/// <summary>One bounded read of a container's log, with both streams already demultiplexed into text.</summary>
public sealed record ContainerLogSnapshot
{
    /// <summary>The log text, standard output and standard error interleaved as the daemon framed them.</summary>
    public required string Text { get; init; }

    /// <summary>
    ///     Whether bytes were discarded to honour the request's ceiling. Reported rather than inferred: a caller that
    ///     had to guess from the length would show a truncated log as a complete one.
    /// </summary>
    public required bool Truncated { get; init; }

    /// <summary>How many lines <see cref="Text" /> holds.</summary>
    public required int LineCount { get; init; }
}
