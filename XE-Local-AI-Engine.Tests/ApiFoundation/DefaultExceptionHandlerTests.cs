namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Tests.Testing;

// The trace id logged for an unhandled exception must be the same W3C id the client receives in
// ProblemDetails.traceId, so an operator can join a client-reported trace id straight to the server log line. The
// Kestrel connection id (HttpContext.TraceIdentifier) is a distinct value and is kept only under RequestId.
public sealed class DefaultExceptionHandlerTests
{
    [Test]
    public async Task TryHandleAsync_LogsSameW3CTraceIdItReturnsInProblemDetails()
    {
        // An active request Activity in W3C id format, exactly as the ASP.NET request pipeline creates one (Program.cs
        // forces the format). Activity.Current then carries a 32-hex trace id distinct from the Kestrel connection id.
        using var activity = new Activity("test-request");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        var expectedTraceId = activity.TraceId.ToString();

        var logger = new StructuredCapturingLogger<DefaultExceptionHandler>();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var handler = new DefaultExceptionHandler(logger, environment);

        const string connectionId = "0HN-kestrel-connection:00000001";
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = connectionId
        };
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/local/v1/diagnostics/exception-probe";

        var handled = await handler.TryHandleAsync(httpContext, new InvalidOperationException("boom"), CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(handled, "The default handler must handle the exception.");
        // WriteAsJsonAsync overwrites Response.ContentType unless the media type is passed explicitly — pin the RFC 7807 type.
        AssertEx.Contains(httpContext.Response.ContentType, "application/problem+json", StringComparison.OrdinalIgnoreCase);

        responseBody.Position = 0;
        using var document = await JsonDocument.ParseAsync(responseBody).ConfigureAwait(false);
        AssertEx.True(document.RootElement.TryGetProperty("traceId", out var traceIdElement), "ProblemDetails must carry a traceId extension.");
        var problemDetailsTraceId = traceIdElement.GetString();

        // The response trace id is the W3C Activity id, and the logged TraceId is the very same value.
        AssertEx.Equal(expectedTraceId, problemDetailsTraceId);
        AssertEx.Equal(expectedTraceId, logger.GetLoggedValue("TraceId")?.ToString());

        // The Kestrel connection id survives, but under RequestId — it never masquerades as the W3C trace id.
        AssertEx.Equal(connectionId, logger.GetLoggedValue("RequestId")?.ToString());
        AssertEx.NotEqual(problemDetailsTraceId, logger.GetLoggedValue("RequestId")?.ToString());
    }

    // Captures the structured state of the most recent log call so a test can read individual message-template values
    // (e.g. TraceId, RequestId) rather than only the rendered string.
    private sealed class StructuredCapturingLogger<T> : ILogger<T>
    {
        private IReadOnlyList<KeyValuePair<string, object?>>? _lastState;

        public object? GetLoggedValue(string key)
        {
            if (_lastState is null)
            {
                return null;
            }

            foreach (var pair in _lastState)
            {
                if (string.Equals(pair.Key, key, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullLoggerScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                _lastState = structured;
            }
        }
    }

    // Kept out of the generic logger so its shared instance is not a static field on a generic type (Sonar S2743).
    private sealed class NullLoggerScope : IDisposable
    {
        public static NullLoggerScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
