namespace XE_Local_AI_Engine.Client.Services.Sandbox.LiveOutput;

/// <summary>The bounds one <see cref="LiveCommandOutput" /> enforces; sizes are UTF-16 characters, each line counted with one separator.</summary>
public sealed class LiveCommandOutputLimits
{
    /// <summary>Lines are coalesced for this long before a batch is delivered, and batches are at least this far apart.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The most one batch carries; a line that does not fit before the next flush is skipped.</summary>
    public int MaxBatchChars { get; init; } = 16 * 1024;

    /// <summary>The most delivered live over the whole command; later lines are only counted as skipped.</summary>
    public int MaxLiveChars { get; init; } = 1024 * 1024;

    /// <summary>The most kept in the tail of the last lines, delivered or skipped.</summary>
    public int MaxTailChars { get; init; } = 16 * 1024;

    /// <summary>The longest line shown; a longer one is cut and marked.</summary>
    public int MaxLineChars { get; init; } = 2000;
}

/// <summary>One redacted output line.</summary>
public sealed class LiveCommandOutputLine
{
    /// <summary>The child stream the line came from.</summary>
    public required SandboxOutputSource Source { get; init; }

    /// <summary>The redacted, ANSI-stripped, length-capped text.</summary>
    public required string Text { get; init; }
}

/// <summary>A coalesced group of redacted lines, in pump order per stream.</summary>
public sealed class LiveCommandOutputBatch
{
    /// <summary>The lines since the previous batch; empty when only the skipped count moved.</summary>
    public required IReadOnlyList<LiveCommandOutputLine> Lines { get; init; }

    /// <summary>All lines skipped so far for this command (cumulative, not since the previous batch).</summary>
    public required int SkippedLines { get; init; }
}
