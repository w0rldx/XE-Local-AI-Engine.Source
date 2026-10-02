namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ProcessCrashHooksTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xe-crash-hooks-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingSink _sink = new();
    private readonly Logger _logger;

    public ProcessCrashHooksTests()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger();
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task UnhandledException_LogsFatal_AndAppendsTheCrashLog()
    {
        var exception = new InvalidOperationException("synthetic-crash");

        ProcessCrashHooks.OnUnhandledException(exception, _logger, _directory);

        var logged = _sink.Events.Single();
        AssertEx.Equal(LogEventLevel.Fatal, logged.Level);
        AssertEx.True(ReferenceEquals(exception, logged.Exception));
        var text = await File.ReadAllTextAsync(Path.Combine(_directory, "startup-crash.log"));
        AssertEx.Contains(text, "System.InvalidOperationException: synthetic-crash");
    }

    [Test]
    public void UnhandledException_WithANonExceptionObject_StillLogs()
    {
        ProcessCrashHooks.OnUnhandledException("not an exception", _logger, crashLogDirectory: null);

        AssertEx.Contains(_sink.Events.Single().Exception?.Message ?? string.Empty, "not an exception");
    }

    [Test]
    public void UnobservedTaskException_LogsError_AndMarksItObserved()
    {
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("synthetic-task")));

        ProcessCrashHooks.OnUnobservedTaskException(args, _logger);

        AssertEx.True(args.Observed);
        var logged = _sink.Events.Single();
        AssertEx.Equal(LogEventLevel.Error, logged.Level);
        AssertEx.True(ReferenceEquals(args.Exception, logged.Exception));
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
