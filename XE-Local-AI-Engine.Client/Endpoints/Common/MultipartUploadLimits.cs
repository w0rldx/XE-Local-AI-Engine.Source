namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     What the multipart upload routes share: the size cap with its body limits, refusal text and 413, and reading the
///     file sections of a body that is not auto-bound.
/// </summary>
internal static class MultipartUploadLimits
{
    // Room for the multipart boundary and part headers around the file, so a file right at the cap is not refused for its envelope.
    internal const long EnvelopeBytes = 64 * 1024;

    internal static long MaxUploadBytes(SecurityOptions options) => options.MaxUploadFileSizeMb * 1024L * 1024L;

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

    /// <summary>Advances to the next FILE section, skipping the nulls the reader yields for every other part.</summary>
    internal static async Task<FileMultipartSection?> NextFileSectionAsync(IAsyncEnumerator<FileMultipartSection?> sections)
    {
        while (await sections.MoveNextAsync())
        {
            if (sections.Current is not null)
            {
                return sections.Current;
            }
        }

        return null;
    }

    // BadHttpRequestException derives from IOException, so this covers a body the server itself refused as well as one
    // the multipart reader ran off the end of. A cancellation is neither, and must keep propagating.
    internal static bool IsMalformedBody(Exception exception) =>
        exception is IOException or InvalidDataException;
}
