namespace XE_Local_AI_Engine.HostAgent.Windows;

using System.Collections.Concurrent;
using System.Globalization;

/// <summary>
///     Provider implementation for rotating file logger behavior.
/// </summary>
public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private const long MaxLogFileBytes = 10 * 1024 * 1024;
    private readonly string _logDirectory;

    private readonly ConcurrentDictionary<string, RotatingFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _writeLock = new();
    private DateOnly _currentDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private int _sequence;

    public RotatingFileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, category => new RotatingFileLogger(category, this));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }

    internal void Write(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception)
    {
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{timestamp:O} [{logLevel}] {categoryName} ({eventId.Id}) {message}{Environment.NewLine}{exception}");

        lock (_writeLock)
        {
            Directory.CreateDirectory(_logDirectory);
            File.AppendAllText(GetCurrentLogPath(), line);
        }
    }

    private string GetCurrentLogPath()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _currentDate)
        {
            _currentDate = today;
            _sequence = 0;
        }

        var path = BuildLogPath(_currentDate, _sequence);
        if (File.Exists(path) && new FileInfo(path).Length >= MaxLogFileBytes)
        {
            _sequence++;
            path = BuildLogPath(_currentDate, _sequence);
        }

        return path;
    }

    private string BuildLogPath(DateOnly date, int sequence)
    {
        return Path.Combine(_logDirectory, $"host-agent-{date:yyyyMMdd}-{sequence:D3}.log");
    }
}

internal sealed class RotatingFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly RotatingFileLoggerProvider _provider;

    public RotatingFileLogger(string categoryName, RotatingFileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= LogLevel.Information;
    }

    public void Log<TState>(LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        _provider.Write(logLevel, _categoryName, eventId, formatter(state, exception), exception);
    }
}

internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    private NullScope()
    {
    }

    public void Dispose()
    {
    }
}
