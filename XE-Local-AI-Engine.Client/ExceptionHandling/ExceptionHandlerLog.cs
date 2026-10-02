namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using XE_Local_AI_Engine.Client.Common.Extensions;

/// <summary>
///     The one log line a failure handler writes for the exception it turned into a problem body, so the trace id the
///     client received finds a server record with the exception attached.
/// </summary>
/// <remarks>
///     Warning for 4xx, Error for 5xx. The NotFound handlers deliberately do not call this: a 404 is an expected
///     answer, and the request-completion line already records it.
/// </remarks>
internal static class ExceptionHandlerLog
{
    public static void Log(ILogger logger, HttpContext httpContext, Exception exception, int statusCode)
    {
        logger.Log(statusCode >= StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Warning,
            exception,
            "Handled exception while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}. ExceptionType: {ExceptionType}",
            RequestLogSanitizer.Sanitize(httpContext.Request.Method),
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value),
            statusCode,
            ProblemDetailsExtensions.ResolveTraceId(httpContext),
            exception.GetType().Name);
    }
}
