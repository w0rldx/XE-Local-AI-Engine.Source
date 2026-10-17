namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Serilog.AspNetCore;
using Serilog.Events;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the request-completion level <see cref="Program.ConfigureRequestLogging" /> installs: a 503 carrying
///     Retry-After is designed backpressure and logs at Information, while any other 5xx or an exception stays Error.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class RequestCompletionLogLevelTests
{
    [Test]
    public void BackpressureRefusal_WithRetryAfter_LogsAtInformation()
    {
        var context = CreateContext(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[HeaderNames.RetryAfter] = "5";

        AssertEx.Equal(LogEventLevel.Information, LevelOf(context, exception: null));
    }

    [Test]
    [Arguments(StatusCodes.Status503ServiceUnavailable)]
    [Arguments(StatusCodes.Status500InternalServerError)]
    public void ServerError_WithoutRetryAfter_LogsAtError(int statusCode)
    {
        AssertEx.Equal(LogEventLevel.Error, LevelOf(CreateContext(statusCode), exception: null));
    }

    [Test]
    public void BackpressureRefusal_WithException_StaysError()
    {
        var context = CreateContext(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[HeaderNames.RetryAfter] = "5";

        AssertEx.Equal(LogEventLevel.Error, LevelOf(context, new InvalidOperationException("boom")));
    }

    private static DefaultHttpContext CreateContext(int statusCode)
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = statusCode;
        return context;
    }

    private static LogEventLevel LevelOf(HttpContext context, Exception? exception)
    {
        var options = new RequestLoggingOptions();
        Program.ConfigureRequestLogging(options);
        return options.GetLevel(context, 1.0, exception);
    }
}
