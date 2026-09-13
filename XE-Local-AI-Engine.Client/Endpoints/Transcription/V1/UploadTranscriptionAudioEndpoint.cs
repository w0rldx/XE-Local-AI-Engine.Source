namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     Streams one audio file into the session and transcribes it, returning the finished session.
/// </summary>
/// <remarks>
///     <para>
///         <b>This endpoint must never bind an <see cref="IFormFile" />.</b> ASP.NET Core buffers a bound form file:
///         anything past the 64 KB memory threshold spills to a framework-owned temp file under
///         <c>ASPNETCORE_TEMP</c> before the handler runs — before the container sniff, before the engine-owned slot
///         exists, and outside the directory the temp-file tests inspect. Almost every real audio file is larger than
///         64 KB, so copying the buffered upload path would put a second plaintext copy of the operator's audio on
///         disk, owned by nobody, on every upload. Auto-binding is therefore disabled and the section is read
///         straight from the multipart stream.
///     </para>
///     <para>
///         <b>One <c>await using</c> spans the copy and the transcription.</b> An overrun, a client disconnect, an
///         <see cref="IOException" /> mid-copy, a cancellation, a failed transcode and a clean success all leave
///         through the same slot disposal, which is the only thing that deletes the audio.
///     </para>
/// </remarks>
public sealed class UploadTranscriptionAudioEndpoint(ITranscriptionService sessions, IOptions<SecurityOptions> securityOptions)
    : Endpoint<UploadTranscriptionAudioRequest, TranscriptionSessionDetailResponse>
{
    private readonly ITranscriptionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly long _maxUploadBytes = (securityOptions ?? throw new ArgumentNullException(nameof(securityOptions))).Value.MaxUploadFileSizeMb * 1024L * 1024L;

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.SessionFile);
        AllowFileUploads(dontAutoBindFormData: true);
        Policies(NodeAuthorizationPolicies.Operator);
        // Form auto-binding is off, so nothing infers the media type from the DTO any more. Declare it explicitly —
        // the same line PreviewSkillImportEndpoint carries, for the same reason.
        Description(builder => builder
                               .Accepts<UploadTranscriptionAudioRequest>("multipart/form-data")
                               .Produces<TranscriptionSessionDetailResponse>(StatusCodes.Status200OK)
                               // This endpoint has no validator, so nothing declares its 400 for it — and it has
                               // three of its own: no file, an unusable file name, and a body past the size cap.
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces(StatusCodes.Status404NotFound)
                               .Produces<TranscriptionUnsupportedContainerResponse>(StatusCodes.Status415UnsupportedMediaType));
    }

    public override async Task HandleAsync(UploadTranscriptionAudioRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Checked BEFORE a byte of the body is read. Without it an upload to a session that does not exist is only
        // refused after the whole file has been streamed to disk — the client waits out a pointless transfer, and the
        // node writes and deletes a file it was never going to use. The outcome mapping below keeps the same answer
        // for the race where the session disappears between this read and the transcription.
        if (await _sessions.GetSessionAsync(req.SessionId, ct).ConfigureAwait(false) is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        // The enumerator is held across the copy on purpose: a multipart section can only be read while it is the
        // reader's current one, so the second file part can only be detected AFTER the first has been consumed.
        var sections = FormFileSectionsAsync(ct).GetAsyncEnumerator(ct);
        await using (sections.ConfigureAwait(false))
        {
            FileMultipartSection? section;
            try
            {
                section = await NextFileSectionAsync(sections).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsMalformedBody(exception))
            {
                // A body that is not a well-formed multipart document is a client error, not a server fault. The
                // degenerate case is a form carrying no parts at all, which the reader reports as a truncated stream —
                // without this arm the caller gets a 500 for having sent nothing.
                section = null;
            }

            if (section is null)
            {
                AddError("A file is required.");
                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;
            }

            await TranscribeSectionAsync(req, section, sections, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Copies one file section into an engine-owned slot and transcribes it.</summary>
    /// <remarks>
    ///     Split out so the one <c>await using</c> that owns the audio is the innermost scope: the slot is created after
    ///     the section is in hand and disposed before the multipart enumerator is, whatever the ending.
    /// </remarks>
    private async Task TranscribeSectionAsync(UploadTranscriptionAudioRequest req,
        FileMultipartSection section,
        IAsyncEnumerator<FileMultipartSection?> sections,
        CancellationToken ct)
    {
        // Reduced to a leaf before anything derives a path from it. Only the EXTENSION is used, and the slot names the
        // file itself, so no client string ever reaches Path.Combine.
        var safeName = UploadFileNameSanitizer.ToSafeLeafFileName(section.FileName);
        if (safeName is null)
        {
            AddError("The file name is invalid.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await using var slot = await _sessions.BeginUploadAsync(req.SessionId, Path.GetExtension(safeName), ct).ConfigureAwait(false);

        try
        {
            // The cap is enforced WHILE the body is read: a streamed upload declares no length beforehand, and a cap
            // checked afterwards has already let every byte reach the disk.
            _ = await slot.CopyFromAsync(section.FileStream ?? section.Section.Body, _maxUploadBytes, ct).ConfigureAwait(false);

            // Reading past the first file answers a question the caller never gets told otherwise: a form carrying two
            // files would have had exactly one of them transcribed, silently, with the second discarded.
            if (await NextFileSectionAsync(sections).ConfigureAwait(false) is not null)
            {
                AddError("Exactly one file is accepted.");
                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;
            }
        }
        catch (TranscriptionUploadTooLargeException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (IsMalformedBody(exception))
        {
            // A body truncated INSIDE the file section throws here rather than at the section read above, and a client
            // that hung up mid-upload is still a client error. The slot's disposal is unaffected: it is the enclosing
            // await using, so the partial audio goes either way. A write failure on the engine's own temp file lands in
            // this arm too, which is the accepted cost of not answering 500 for a truncated upload.
            AddError("The uploaded file could not be read.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var result = await _sessions.TranscribeFileAsync(slot, ct).ConfigureAwait(false);
        if (result.Outcome == TranscribeFileOutcome.UnsupportedContainer)
        {
            await SendUnsupportedContainerAsync(result).ConfigureAwait(false);
            return;
        }

        if (result.Outcome == TranscribeFileOutcome.SessionNotFound)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        // Succeeded, Cancelled and RuntimeFailed are all FINISHED requests whose verdict lives on the session row —
        // Completed, Cancelled or Failed with a sanitized reason. Answering 200 with that row is what lets one client
        // render every ending the same way, instead of translating a status code back into a session state. The
        // fallback pair carries the one refusal that writes nothing to the row ("already-transcribing").
        var session = result.Session ?? await _sessions.GetSessionAsync(req.SessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(session.ToResponse(result.ErrorCode, result.ErrorMessage), ct).ConfigureAwait(false);
    }

    /// <summary>Advances to the next FILE section, skipping the nulls the reader yields for every other part.</summary>
    private static async Task<FileMultipartSection?> NextFileSectionAsync(IAsyncEnumerator<FileMultipartSection?> sections)
    {
        while (await sections.MoveNextAsync().ConfigureAwait(false))
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
    private static bool IsMalformedBody(Exception exception) => exception is IOException or InvalidDataException;

    private Task SendUnsupportedContainerAsync(TranscribeFileResult result) =>
        Send.ResultAsync(Results.Json(new TranscriptionUnsupportedContainerResponse
            {
                // A container this node could decode WITH ffmpeg is a different problem from one it can never decode,
                // and only the first is something the operator can fix.
                Reason = result.FfmpegRequired ? "ffmpeg-required" : "unsupported-container",
                Message = result.ErrorMessage ?? "This node cannot transcribe that audio.",
                DetectedContainer = result.DetectedContainer.ToString(),
                SupportedContainers = result.SupportedContainers,
                FfmpegRequired = result.FfmpegRequired
            },
            statusCode: StatusCodes.Status415UnsupportedMediaType));
}
