namespace XE_Local_AI_Engine.Client.Testing.Fakes;

using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
///     Minimal <see cref="ILogger{TCategoryName}" /> that keeps every formatted entry, so a test can assert that a
///     best-effort service swallowed a dependency failure and reported it instead of crashing the host.
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly StringBuilder _allText = new();
    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>
    ///     Snapshot of every entry logged so far. Hosted-service tests read this from the test thread while the service
    ///     logs from a background task, so the backing list is guarded and each read returns a stable copy.
    /// </summary>
    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>
    ///     Every formatted message, logged exception and scope state so far, one per line in arrival order, so a test can
    ///     assert that secret material appears nowhere in the log.
    /// </summary>
    public string AllText
    {
        get
        {
            lock (_gate)
            {
                return _allText.ToString();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        lock (_gate)
        {
            _allText.AppendLine(state.ToString());
        }

        return null;
    }

    public bool IsEnabled(LogLevel logLevel) =>
        true;

    public void Log<TState>(LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var entry = new Entry
        {
            Level = logLevel,
            EventId = eventId,
            Message = formatter(state, exception),
            Exception = exception
        };
        lock (_gate)
        {
            _entries.Add(entry);
            _allText.AppendLine(entry.Message);
            if (exception is not null)
            {
                _allText.AppendLine(exception.ToString());
            }
        }
    }

    public bool HasEntry(LogLevel level, string messageFragment) =>
        Entries.Any(entry => entry.Level == level && entry.Message.Contains(messageFragment, StringComparison.Ordinal));

    /// <summary>How many messages contain <paramref name="fragment" />, at <paramref name="level" /> when one is given.</summary>
    public int CountContaining(string fragment, LogLevel? level = null) =>
        Entries.Count(entry => (level is null || entry.Level == level) && entry.Message.Contains(fragment, StringComparison.Ordinal));

    public sealed class Entry
    {
        public required LogLevel Level { get; init; }

        public EventId EventId { get; init; }

        public required string Message { get; init; }

        public required Exception? Exception { get; init; }
    }
}
