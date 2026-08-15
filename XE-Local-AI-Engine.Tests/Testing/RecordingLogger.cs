namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.Logging;

/// <summary>
///     Minimal <see cref="ILogger{TCategoryName}" /> that keeps every formatted entry, so a test can assert that a
///     best-effort service swallowed a dependency failure and reported it instead of crashing the host. Several test
///     classes previously carried a private copy of this; new tests share this one.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<Entry> Entries { get; } = [];

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
        Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }

    public bool HasEntry(LogLevel level, string messageFragment) =>
        Entries.Exists(entry => entry.Level == level && entry.Message.Contains(messageFragment, StringComparison.Ordinal));

    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);
}
