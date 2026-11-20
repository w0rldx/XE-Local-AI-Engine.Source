namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Globalization;

/// <summary>What one read of a <c>/proc/[pid]/stat</c> file proves.</summary>
public enum LinuxProcStatOutcome
{
    /// <summary>The file parsed.</summary>
    Parsed,

    /// <summary>The file or its <c>/proc/[pid]</c> directory does not exist: no process holds the pid.</summary>
    Missing,

    /// <summary>Any other read failure, or content that does not parse: proves nothing about the pid.</summary>
    Unreadable
}

/// <summary>
///     The identity fields of one <c>/proc/[pid]/stat</c> read, parsed once for the process-supervision reaper and the training
///     inspector.
/// </summary>
/// <remarks>The fields are meaningful only when <see cref="Outcome" /> is <see cref="LinuxProcStatOutcome.Parsed" />.</remarks>
public sealed class LinuxProcStat
{
    public static readonly LinuxProcStat Missing = new()
    {
        Outcome = LinuxProcStatOutcome.Missing
    };

    public static readonly LinuxProcStat Unreadable = new()
    {
        Outcome = LinuxProcStatOutcome.Unreadable
    };

    public required LinuxProcStatOutcome Outcome { get; init; }

    /// <summary>Field 4.</summary>
    public int ParentProcessId { get; init; }

    /// <summary>Field 5.</summary>
    public int ProcessGroupId { get; init; }

    /// <summary>Field 22, clock ticks after boot: survives exec and changes on pid reuse.</summary>
    public long StartTicks { get; init; }

    /// <summary>State (field 3) Z (zombie) or X (dead): the process runs nothing and holds no memory; only its parent's wait is left.</summary>
    public bool Exited { get; init; }

    /// <summary>Reads and parses one stat file. Never throws for a read failure: missing is <see cref="Missing" />, anything else <see cref="Unreadable" />.</summary>
    public static LinuxProcStat Read(string path)
    {
        string raw;
        try
        {
            // Forced sync: both callers are synchronous contracts (the startup reaper and scanner, the training Inspect); the source is
            // procfs, which never blocks on a device.
#pragma warning disable MA0045 // Contract-forced: sync reaper, scanner and Inspect contracts, procfs read.
            raw = File.ReadAllText(path);
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unreadable;
        }

        // Field 2 (comm) is parenthesized and may contain spaces and parentheses, so the rest is located from the LAST ')'. Tokens then
        // start at field 3 (state), putting state at index 0, ppid at index 1, pgrp at index 2 and starttime at index 19.
        var commEnd = raw.LastIndexOf(')');
        if (commEnd < 0 || commEnd + 2 >= raw.Length)
        {
            return Unreadable;
        }

        var fields = raw[(commEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int stateIndex = 0;
        const int parentIndex = 1;
        const int groupIndex = 2;
        const int startTimeIndex = 19;
        if (fields.Length <= startTimeIndex
            || !int.TryParse(fields[parentIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent)
            || !int.TryParse(fields[groupIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)
            || !long.TryParse(fields[startTimeIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks))
        {
            return Unreadable;
        }

        return new LinuxProcStat
        {
            Outcome = LinuxProcStatOutcome.Parsed,
            ParentProcessId = parent,
            ProcessGroupId = group,
            StartTicks = startTicks,
            Exited = fields[stateIndex] is "Z" or "X"
        };
    }
}
