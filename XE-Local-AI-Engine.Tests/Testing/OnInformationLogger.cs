namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.Logging;

/// <summary>
///     Runs a callback once, on the logging thread, when an Information entry whose message template starts with the given text is
///     logged: a barrier at the exact point in product code that logs it.
/// </summary>
/// <remarks>Matches the structured <c>{OriginalFormat}</c> template, so a change to a placeholder's formatting cannot move the seam.</remarks>
internal sealed class OnInformationLogger<T> : ILogger<T>
{
    private readonly string _templatePrefix;
    private Action? _onInformation;

    public OnInformationLogger(string templatePrefix, Action onInformation)
    {
        _templatePrefix = templatePrefix;
        _onInformation = onInformation;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) =>
        true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Information
            && state is IReadOnlyList<KeyValuePair<string, object?>> fields
            && fields.Any(field => field.Key == "{OriginalFormat}" && field.Value is string template && template.StartsWith(_templatePrefix, StringComparison.Ordinal)))
        {
            Interlocked.Exchange(ref _onInformation, value: null)?.Invoke();
        }
    }
}
