namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that records every formatted message + scope so tests can assert
/// that token material never appears in any log line (plan §9/§12).
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly StringBuilder _builder = new();
    private readonly Lock _gate = new();

    /// <summary>All formatted log text captured so far, newline-joined.</summary>
    public string AllText
    {
        get
        {
            lock (_gate)
            {
                return _builder.ToString();
            }
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        lock (_gate)
        {
            _builder.AppendLine(state.ToString());
        }

        return CapturingLoggerScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_gate)
        {
            _builder.AppendLine(formatter(state, exception));
            if (exception is not null)
            {
                _builder.AppendLine(exception.ToString());
            }
        }
    }

}

/// <summary>A no-op scope shared by every <see cref="CapturingLogger{T}"/> instance.</summary>
internal sealed class CapturingLoggerScope : IDisposable
{
    public static CapturingLoggerScope Instance { get; } = new();

    public void Dispose()
    {
    }
}
