namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     Accepts one PNG or JPEG as an edit source and stores it encrypted at rest. Operator-gated.
/// </summary>
/// <remarks>
///     <b>Never binds an <see cref="IFormFile" />:</b> a bound form file past 64 KB is buffered to a framework-owned
///     plaintext temp file. The single file section is copied into a capped in-memory buffer instead, so a personal photo
///     never touches disk unencrypted. Format and size come from the bytes (<see cref="UploadedImageService" />).
/// </remarks>
public sealed class UploadImageEndpoint : Endpoint<UploadImageRequest, UploadedImageResponse>
{
    private readonly UploadedImageService _uploads;
    private readonly long _maxUploadBytes;

    public UploadImageEndpoint(UploadedImageService uploads, IOptions<SecurityOptions> securityOptions)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        _uploads = uploads;
        ArgumentNullException.ThrowIfNull(securityOptions);
        _maxUploadBytes = MultipartUploadLimits.MaxUploadBytes(securityOptions.Value);
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.Uploads);
        AllowFileUploads(dontAutoBindFormData: true);
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(_maxUploadBytes + MultipartUploadLimits.EnvelopeBytes)));
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<UploadImageRequest>("multipart/form-data")
                               .Produces<UploadedImageResponse>(StatusCodes.Status200OK)
                               // No validator declares the 400 for this endpoint: no file, two files, a body past the cap, or bytes that are not an accepted image.
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(UploadImageRequest req, CancellationToken ct)
    {
        if (HttpContext.Request.ContentLength > _maxUploadBytes + MultipartUploadLimits.EnvelopeBytes)
        {
            await Send.ResultAsync(MultipartUploadLimits.TooLarge(_maxUploadBytes));
            return;
        }

        byte[]? bytes;
        // The enumerator is held across the copy: the second file part can only be detected once the first is consumed.
        var sections = FormFileSectionsAsync(ct).GetAsyncEnumerator(ct);
        await using (sections)
        {
            try
            {
                var section = await MultipartUploadLimits.NextFileSectionAsync(sections);
                if (section is null)
                {
                    await SendErrorAsync("A file is required.", ct);
                    return;
                }

                bytes = await ReadCappedAsync(section.FileStream ?? section.Section.Body, _maxUploadBytes, ct);
                if (bytes is null)
                {
                    await Send.ResultAsync(MultipartUploadLimits.TooLarge(_maxUploadBytes));
                    return;
                }

                if (await MultipartUploadLimits.NextFileSectionAsync(sections) is not null)
                {
                    await SendErrorAsync("Exactly one file is accepted.", ct);
                    return;
                }
            }
            catch (Exception exception) when (MultipartUploadLimits.IsMalformedBody(exception))
            {
                // A body that is not a well-formed multipart document (or was cut off mid-file) is a client error.
                await SendErrorAsync("The uploaded file could not be read.", ct);
                return;
            }
        }

        GeneratedImageInfo info;
        try
        {
            info = await _uploads.AddAsync(bytes, ct);
        }
        catch (ImageUploadRejectedException exception)
        {
            await SendErrorAsync(exception.Message, ct);
            return;
        }

        await Send.OkAsync(info.ToUploadedResponse(), ct);
    }

    /// <summary>Copies the stream into memory, or returns <see langword="null" /> as soon as it passes <paramref name="cap" />.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream source, long cap, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }

    private async Task SendErrorAsync(string message, CancellationToken ct)
    {
        AddError(message);
        await Send.ErrorsAsync(cancellation: ct);
    }
}
