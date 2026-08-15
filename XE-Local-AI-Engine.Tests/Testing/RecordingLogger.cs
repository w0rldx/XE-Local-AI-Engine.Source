namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.Logging;

/// <summary>
///     Minimal <see cref="ILogger{TCategoryName}" /> that keeps every formatted entry, so a test can assert that a
///     best-effort service swallowed a dependency failure and reported it instead of crashing the host. Several test
///     classes previously carried a private copy of this; new tests share this one.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
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

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) =>
        true;

    public void Log<TState>(LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var entry = new Entry(logLevel, formatter(state, exception), exception);
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public bool HasEntry(LogLevel level, string messageFragment) =>
        Entries.Any(entry => entry.Level == level && entry.Message.Contains(messageFragment, StringComparison.Ordinal));

    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);
}
