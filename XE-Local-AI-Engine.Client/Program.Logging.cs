namespace XE_Local_AI_Engine.Client;

using Serilog.AspNetCore;
using Serilog.Events;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed partial class Program
{
    // Request-completion level for UseSerilogRequestLogging: failures stay loud (5xx/exception = Error, unexpected 4xx =
    // Warning) while routine traffic (2xx/3xx, the 401 token-refresh dance, SPA-fallback 404s) drops to Debug so the SPA's
    // polling does not dominate the rolling log file.
    internal static void ConfigureRequestLogging(RequestLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.EnrichDiagnosticContext = static (diagnosticContext, httpContext) =>
        {
            var redactedQuery = AccessTokenQueryRedactor.Redact(httpContext.Request.QueryString.Value);
            diagnosticContext.Set("RequestPathWithRedactedQuery", $"{httpContext.Request.Path}{redactedQuery}");
            diagnosticContext.Set("QueryString", redactedQuery);
        };

        // Keep failures loud but drop routine traffic below the default Information floor: the SPA polls several
        // endpoints (auth status, download/job progress, health), so at Information the request log dominates the
        // rolling file (~60% of all lines measured) and buries the diagnostic entries the file sink exists for.
        // 401 (routine token-refresh dance) and 404 (SPA fallback probing) stay at Debug with the successes.
        options.GetLevel = static (httpContext, _, ex) => GetRequestCompletionLogLevel(httpContext, ex);

        // Serilog.AspNetCore derives its default completion properties directly from IHttpRequestFeature. Override
        // those log-event values at the middleware's supported property boundary rather than mutating routing inputs.
        options.GetMessageTemplateProperties = static (httpContext, requestPath, elapsedMilliseconds, statusCode) =>
        [
            new LogEventProperty("RequestMethod", new ScalarValue(RequestLogSanitizer.Sanitize(httpContext.Request.Method))),
            new LogEventProperty("RequestPath", new ScalarValue(RequestLogSanitizer.Sanitize(requestPath))),
            new LogEventProperty("StatusCode", new ScalarValue(statusCode)),
            new LogEventProperty("Elapsed", new ScalarValue(elapsedMilliseconds))
        ];
    }

    private static LogEventLevel GetRequestCompletionLogLevel(HttpContext httpContext, Exception? exception)
    {
        if (exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogEventLevel.Error;
        }

        return httpContext.Response.StatusCode is >= StatusCodes.Status400BadRequest
            and not StatusCodes.Status401Unauthorized
            and not StatusCodes.Status404NotFound
            ? LogEventLevel.Warning
            : LogEventLevel.Debug;
    }
}
