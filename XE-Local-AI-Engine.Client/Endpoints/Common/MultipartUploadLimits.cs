namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using Microsoft.AspNetCore.Mvc;
using XE_Local_AI_Engine.Client.ExceptionHandling;

/// <summary>The size cap the multipart upload routes share: its body limits, its refusal text and its 413.</summary>
internal static class MultipartUploadLimits
{
    // Room for the multipart boundary and part headers around the file, so a file right at the cap is not refused for its envelope.
    internal const long EnvelopeBytes = 64 * 1024;

    internal static string TooLargeMessage(long maxUploadBytes) =>
        $"The file exceeds the maximum upload size of {maxUploadBytes / (1024L * 1024L)} MB.";

    /// <summary>The 413 an upload route answers for a file past its cap, built by the one writer the host's own refusal uses.</summary>
    internal static IResult TooLarge(long maxUploadBytes) =>
        RequestBodyTooLargeProblem.Result(TooLargeMessage(maxUploadBytes));

    /// <summary>
    ///     Raises the host and form-reader limits of an auto-bound upload route to the cap plus the envelope, and refuses a
    ///     declared length past them before form binding, which would otherwise answer "malformed form data".
    /// </summary>
    internal static void Apply(RouteHandlerBuilder builder, long maxUploadBytes)
    {
        var limit = maxUploadBytes + EnvelopeBytes;
        builder.WithMetadata(new RequestSizeLimitAttribute(limit),
            new RequestFormLimitsAttribute
            {
                MultipartBodyLengthLimit = limit
            });
        builder.ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.ContentLength > limit ? TooLarge(maxUploadBytes) : await next(context));
    }
}
