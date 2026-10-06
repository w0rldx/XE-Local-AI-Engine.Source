namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Shared HTTP mapping for the synchronous failures
///     <see cref="Services.ModelFit.IGgufDownloadCoordinator.StartAsync" /> can surface, so every endpoint that starts
///     a GGUF download answers with the same status codes instead of a 500.
/// </summary>
/// <remarks>
///     Mirrors <see cref="GgufImportEndpointSupport" /> for the import pipeline. Only the sanitized exception surfaces
///     reach here (<see cref="GgufAcquisitionConflictException" /> and <see cref="HuggingFaceDownloadException" />);
///     anything else is left to the global handler as a 500.
/// </remarks>
internal static class GgufDownloadEndpointSupport
{
    /// <summary>True when <paramref name="exception" /> has a mapped status code — use as the <c>when</c> filter.</summary>
    public static bool IsHandled(Exception exception) =>
        exception is GgufAcquisitionConflictException or HuggingFaceDownloadException;

    /// <summary>Maps a handled start failure to ProblemDetails. Throws for anything <see cref="IsHandled" /> rejects.</summary>
    public static IResult Error(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            GgufAcquisitionConflictException conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: conflict.Message),
            HuggingFaceDownloadException download => Download(download),
            _ => throw new ArgumentException("The exception has no mapped GGUF download status code.", nameof(exception))
        };
    }

    private static IResult Download(HuggingFaceDownloadException exception)
    {
        var (statusCode, title) = exception.Reason switch
        {
            HuggingFaceDownloadFailure.DestinationConflict or HuggingFaceDownloadFailure.HashMismatch =>
                new DownloadProblem(StatusCodes.Status409Conflict, "The repository did not provide exact metadata compatible with this acquisition."),
            HuggingFaceDownloadFailure.NotFound =>
                new DownloadProblem(StatusCodes.Status404NotFound, "The requested repository, revision, or file was not found."),
            HuggingFaceDownloadFailure.Gated or HuggingFaceDownloadFailure.Unauthorized =>
                new DownloadProblem(StatusCodes.Status403Forbidden, "The repository is gated or the configured Hugging Face token was rejected."),
            HuggingFaceDownloadFailure.DiskFull =>
                new DownloadProblem(StatusCodes.Status507InsufficientStorage, "There is not enough free disk space for the selected model."),
            _ => new DownloadProblem(StatusCodes.Status503ServiceUnavailable, "Hugging Face could not be reached.")
        };
        return Results.Problem(statusCode: statusCode, title: title, detail: exception.Message);
    }

    /// <summary>The HTTP status and problem title a download failure maps to.</summary>
    private readonly record struct DownloadProblem(int StatusCode, string Title);
}
