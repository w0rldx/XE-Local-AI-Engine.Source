namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using FluentValidation.Results;
using ProblemDetails = FastEndpoints.ProblemDetails;

/// <summary>
///     Writes the body FastEndpoints' own <c>AddError(message) + Send.ErrorsAsync(statusCode)</c> pair writes, from a
///     global <see cref="Microsoft.AspNetCore.Diagnostics.IExceptionHandler" /> that has no endpoint to call it on.
/// </summary>
/// <remarks>
///     The same <see cref="ProblemDetails" /> DTO built the way <c>ErrorOptions.ResponseBuilder</c> builds it
///     (instance = request path, traceId = <see cref="HttpContext.TraceIdentifier" />), serialized with the same
///     options and content type. Every handler that replaces a per-endpoint <c>AddError</c> catch goes through here, so
///     the replacement stays byte-identical in one place rather than once per handler.
/// </remarks>
internal static class FastEndpointsProblemWriter
{
    // FastEndpoints' ErrorOptions.GeneralErrorsField default — the property AddError(message) attaches a message-only failure to.
    // Its getter is internal, so the literal is mirrored; FE's ProblemDetails.Error ctor runs it through the serializer's naming policy, yielding "generalErrors" on the wire.
    private const string GeneralErrorsField = "GeneralErrors";

    // ErrorOptions.ContentType + SerializerOptions.CharacterEncoding, the pair FE's ResponseSerializer concatenates.
    private const string ProblemContentType = "application/problem+json; charset=utf-8";

    public static Task WriteAsync(HttpContext httpContext, string message, int statusCode, CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails([new ValidationFailure(GeneralErrorsField, message)],
            httpContext.Request.Path,
            httpContext.TraceIdentifier,
            statusCode);

        // Serialized as object (like FE's ResponseSerializer) against the DI json options, which ConfigureServices
        // configures with the very same ConfigureJsonSerializerOptions that seeds FastEndpoints' Config.Serializer.
        return httpContext.Response.WriteAsJsonAsync<object>(problemDetails, options: null, ProblemContentType, cancellationToken);
    }
}
