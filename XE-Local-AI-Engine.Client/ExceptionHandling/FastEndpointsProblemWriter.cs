namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using FluentValidation.Results;
using XE_Local_AI_Engine.Client.Common.Extensions;
using ProblemDetails = FastEndpoints.ProblemDetails;

/// <summary>
///     Writes the body FastEndpoints' own <c>AddError(message) + Send.ErrorsAsync(statusCode)</c> pair writes, from a
///     global <see cref="Microsoft.AspNetCore.Diagnostics.IExceptionHandler" /> that has no endpoint to call it on.
/// </summary>
/// <remarks>
///     FastEndpoints' own <see cref="ProblemDetails" /> shape, options and content type, so every handler replacing an
///     <c>AddError</c> catch stays byte-identical in one place. One change: traceId is the W3C id
///     (<see cref="ProblemDetailsExtensions.ResolveTraceId" />) the log prints, not FE's connection id. <see cref="Build" />
///     is also the host's <c>ResponseBuilder</c>, so validator 400s carry it too.
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

        var problemDetails = Build([new ValidationFailure(GeneralErrorsField, message)], httpContext, statusCode);

        // Serialized as object (like FE's ResponseSerializer) against the DI json options, which ConfigureServices
        // configures with the very same ConfigureJsonSerializerOptions that seeds FastEndpoints' Config.Serializer.
        return httpContext.Response.WriteAsJsonAsync<object>(problemDetails, options: null, ProblemContentType, cancellationToken);
    }

    /// <summary>FastEndpoints' default <c>ResponseBuilder</c> with the W3C trace id in place of the connection id.</summary>
    public static ProblemDetails Build(List<ValidationFailure> failures, HttpContext httpContext, int statusCode)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return new ProblemDetails(failures, httpContext.Request.Path, ProblemDetailsExtensions.ResolveTraceId(httpContext), statusCode);
    }
}
