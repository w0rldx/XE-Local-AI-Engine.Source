namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

/// <summary>
///     Node-wide singleton holding the output tails of the most recent child processes, exited ones included, so a
///     support bundle can show what a runtime said before it died.
/// </summary>
/// <remarks>
///     Keeps at most <see cref="MaxEntries" />: when full, the oldest exited entry is dropped first, then the oldest
///     running one. Each tail is sanitized on the way in (see <see cref="ProcessStderrTail" />).
/// </remarks>
public sealed class ChildProcessOutputTailRegistry : IChildProcessOutputTails
{
    /// <summary>Most entries retained.</summary>
    public const int MaxEntries = 8;

    /// <summary>Line bound of a tail created by <see cref="Register(string)" />.</summary>
    public const int TailMaxLines = 200;

    /// <summary>Character bound of a tail created by <see cref="Register(string)" />.</summary>
    public const int TailMaxCharacters = 32 * 1024;

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the registry, stamping entries with the node's registered <paramref name="timeProvider" />.</summary>
    public ChildProcessOutputTailRegistry(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>Creates and tracks a new 200-line / 32 KB tail for a freshly spawned process.</summary>
    public ProcessStderrTail Register(string label)
    {
        var tail = new ProcessStderrTail(TailMaxLines, TailMaxCharacters);
        Register(label, tail);
        return tail;
    }

    /// <summary>Tracks a tail the launcher already owns.</summary>
    public void Register(string label, ProcessStderrTail tail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(tail);

        lock (_gate)
        {
            _entries.Add(new Entry(label, tail, _timeProvider.GetUtcNow()));
            while (_entries.Count > MaxEntries)
            {
                var exited = _entries.FindIndex(static e => e.ExitedUtc is not null);
                _entries.RemoveAt(exited >= 0 ? exited : 0);
            }
        }
    }

    /// <summary>Stamps the exit time of <paramref name="tail" />'s entry; idempotent, a no-op once evicted.</summary>
    public void MarkExited(ProcessStderrTail tail)
    {
        ArgumentNullException.ThrowIfNull(tail);

        lock (_gate)
        {
            var entry = _entries.Find(e => ReferenceEquals(e.Tail, tail));
            if (entry is not null)
            {
                entry.ExitedUtc ??= _timeProvider.GetUtcNow();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ChildProcessOutputTail> Snapshot()
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries];
        }

        var result = new ChildProcessOutputTail[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[entries.Length - 1 - i];
            result[i] = new ChildProcessOutputTail
            {
                Label = e.Label,
                StartedUtc = e.StartedUtc,
                ExitedUtc = e.ExitedUtc,
                Lines = e.Tail.Lines()
            };
        }

        return result;
    }

    private sealed class Entry
    {
        public Entry(string label, ProcessStderrTail tail, DateTimeOffset startedUtc)
        {
            Label = label;
            Tail = tail;
            StartedUtc = startedUtc;
        }

        public string Label { get; }
        public ProcessStderrTail Tail { get; }
        public DateTimeOffset StartedUtc { get; }
        public DateTimeOffset? ExitedUtc { get; set; }
    }
}
