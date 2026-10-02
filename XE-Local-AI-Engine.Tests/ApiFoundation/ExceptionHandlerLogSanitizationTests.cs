namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Hosting;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class ExceptionHandlerLogSanitizationTests
{
    private const string UnsafeMethod = "PO\rST\0\u0085";
    private const string UnsafePath = "/safe\npath\u007F\u2028next\u2029line";
    private const string ExpectedMethod = "PO\\u000DST\\u0000\\u0085";
    private const string ExpectedPath = "/safe\\u000Apath\\u007F\\u2028next\\u2029line";

    [Test]
    public void RequestLogSanitizer_EscapesEveryUnicodeControlCharacter()
    {
        var controls = string.Concat(Enumerable.Range(char.MinValue, char.MaxValue + 1)
                                               .Select(static value => (char)value)
                                               .Where(char.IsControl));

        var sanitized = RequestLogSanitizer.Sanitize(controls);

        AssertEx.False(sanitized.Any(IsLineBreakingOrControlCharacter));
        foreach (var control in controls)
        {
            AssertEx.Contains(sanitized, $"\\u{(int)control:X4}", StringComparison.Ordinal);
        }

        AssertEx.Equal("\\u0085\\u2028\\u2029", RequestLogSanitizer.Sanitize("\u0085\u2028\u2029"));
        AssertEx.Equal(string.Empty, RequestLogSanitizer.Sanitize(null));
        AssertEx.Equal(string.Empty, RequestLogSanitizer.Sanitize(string.Empty));
        AssertEx.Equal("ordinary/value", RequestLogSanitizer.Sanitize("ordinary/value"));
    }

    [Test]
    public async Task SerilogRequestCompletion_SanitizesMethodAndPathWithoutMutatingRoutingInputs()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new DiagnosticContext(logger));

        await using var app = builder.Build();
        app.UseSerilogRequestLogging(options =>
        {
            Program.ConfigureRequestLogging(options);
            options.Logger = logger;
        });
        app.Run(static context =>
        {
            AssertEx.Equal(UnsafeMethod, context.Request.Method);
            AssertEx.Equal(UnsafePath, context.Request.Path.Value);
            return Task.CompletedTask;
        });
        await app.StartAsync();

        await app.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = UnsafeMethod;
            context.Request.Path = UnsafePath;
        });

        AssertEx.Equal(expected: 1, sink.Events.Count);
        var completion = sink.Events.Single();
        AssertEx.Equal(ExpectedMethod, GetScalarString(completion, "RequestMethod"));
        AssertEx.Equal(ExpectedPath, GetScalarString(completion, "RequestPath"));
        AssertEx.False(completion.RenderMessage(CultureInfo.InvariantCulture).Any(IsLineBreakingOrControlCharacter));
    }

    [Test]
    public async Task DefaultHandler_SanitizesRequestLogProperties()
    {
        var logger = new StructuredCapturingLogger<DefaultExceptionHandler>();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var handler = new DefaultExceptionHandler(logger, environment);
        var context = CreateContext();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        AssertSanitizedRequestProperties(handled, logger);
        AssertEx.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Test]
    public async Task ConflictHandler_SanitizesRequestLogProperties()
    {
        var logger = new StructuredCapturingLogger<ConflictExceptionHandler>();
        var handler = new ConflictExceptionHandler(logger);
        var context = CreateContext();
        using var activity = StartW3CActivity();

        var handled = await handler.TryHandleAsync(context,
            new WorkSessionInvalidTransitionException("A work session cannot be deleted mid-step."),
            CancellationToken.None);

        AssertSanitizedRequestProperties(handled, logger);
        AssertEx.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        await AssertTraceIdJoinsAsync(logger, context, activity);
    }

    [Test]
    public async Task DomainValidationHandler_SanitizesRequestLogProperties()
    {
        var logger = new StructuredCapturingLogger<DomainValidationExceptionHandler>();
        var handler = new DomainValidationExceptionHandler(logger);
        var context = CreateContext();
        using var activity = StartW3CActivity();

        var handled = await handler.TryHandleAsync(context, new ScheduledJobValidationException("invalid"), CancellationToken.None);

        AssertSanitizedRequestProperties(handled, logger);
        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        await AssertTraceIdJoinsAsync(logger, context, activity);
    }

    [Test]
    public async Task DevelopmentConflictHandler_LogsTheW3CTraceIdOfTheProblemBody()
    {
        var logger = new StructuredCapturingLogger<DevelopmentConflictExceptionHandler>();
        var handler = new DevelopmentConflictExceptionHandler(logger);
        var context = CreateContext();
        using var activity = StartW3CActivity();

        var handled = await handler.TryHandleAsync(context, new DevelopmentConcurrencyException("stale"), CancellationToken.None);

        AssertSanitizedRequestProperties(handled, logger);
        AssertEx.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        await AssertTraceIdJoinsAsync(logger, context, activity);
    }

    [Test]
    public async Task RequestBodyTooLargeHandler_LogsTheW3CTraceIdOfTheProblemBody()
    {
        var logger = new StructuredCapturingLogger<RequestBodyTooLargeExceptionHandler>();
        var handler = new RequestBodyTooLargeExceptionHandler(logger);
        var context = CreateContext();
        using var activity = StartW3CActivity();

        var handled = await handler.TryHandleAsync(context,
            new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge),
            CancellationToken.None);

        AssertSanitizedRequestProperties(handled, logger);
        AssertEx.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        await AssertTraceIdJoinsAsync(logger, context, activity);
    }

    // A 4xx the failure handlers turn into a problem body logs at Warning with the W3C trace id the body carries.
    [Test]
    public async Task SelectedFolderHandler_ValidationFailure_LogsWarningWithTraceIdAndSanitizedPath()
    {
        var logger = new StructuredCapturingLogger<SelectedFolderExceptionHandler>();
        var handler = new SelectedFolderExceptionHandler(logger);
        var context = CreateContext();
        using var activity = StartW3CActivity();

        var handled = await handler.TryHandleAsync(context, new SelectedFolderValidationException("bad folder"), CancellationToken.None);

        AssertSanitizedRequestProperties(handled, logger);
        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        AssertEx.Equal(LogLevel.Warning, logger.LastLevel);
        await AssertTraceIdJoinsAsync(logger, context, activity);
        AssertEx.True(logger.LastException is SelectedFolderValidationException, "the exception must ride on the log line");
    }

    // A 5xx logs at Error.
    [Test]
    public async Task ContainerRuntimeUnavailableHandler_LogsErrorWithTraceIdAndSanitizedPath()
    {
        var logger = new StructuredCapturingLogger<ContainerRuntimeUnavailableExceptionHandler>();
        var handler = new ContainerRuntimeUnavailableExceptionHandler(logger);
        var context = CreateContext();
        using var activity = StartW3CActivity();

        var handled = await handler.TryHandleAsync(context, new ContainerRuntimeUnavailableException("no daemon"), CancellationToken.None);

        AssertSanitizedRequestProperties(handled, logger);
        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Equal(LogLevel.Error, logger.LastLevel);
        await AssertTraceIdJoinsAsync(logger, context, activity);
        AssertEx.True(logger.LastException is ContainerRuntimeUnavailableException, "the exception must ride on the log line");
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = UnsafeMethod;
        context.Request.Path = UnsafePath;
        return context;
    }

    private static void AssertSanitizedRequestProperties<T>(bool handled, StructuredCapturingLogger<T> logger)
    {
        AssertEx.True(handled);
        AssertEx.Equal(ExpectedMethod, logger.GetLoggedValue("Method")?.ToString());
        AssertEx.Equal(ExpectedPath, logger.GetLoggedValue("Path")?.ToString());
    }

    private static Activity StartW3CActivity()
    {
        var activity = new Activity("exception-handler-log-test");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        return activity;
    }

    // The logged TraceId, the problem body's traceId and the request Activity's W3C trace id are one value.
    private static async Task AssertTraceIdJoinsAsync<T>(StructuredCapturingLogger<T> logger, DefaultHttpContext context, Activity activity)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: CancellationToken.None);
        var traceId = activity.TraceId.ToString();
        AssertEx.Equal(traceId, logger.GetLoggedValue("TraceId")?.ToString());
        AssertEx.Equal(traceId, document.RootElement.GetProperty("traceId").GetString());
    }

    private static bool IsLineBreakingOrControlCharacter(char character) =>
        char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static string? GetScalarString(LogEvent logEvent, string propertyName) =>
        (logEvent.Properties[propertyName] as ScalarValue)?.Value as string;

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
            {
                Events.Add(logEvent);
            }
        }
    }

    private sealed class StructuredCapturingLogger<T> : ILogger<T>
    {
        private IReadOnlyList<KeyValuePair<string, object?>>? _lastState;

        public LogLevel? LastLevel { get; private set; }

        public Exception? LastException { get; private set; }

        public object? GetLoggedValue(string key)
        {
            return _lastState?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.Ordinal)).Value;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullLoggerScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                _lastState = structured;
                LastLevel = logLevel;
                LastException = exception;
            }
        }
    }

    private sealed class NullLoggerScope : IDisposable
    {
        public static NullLoggerScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
