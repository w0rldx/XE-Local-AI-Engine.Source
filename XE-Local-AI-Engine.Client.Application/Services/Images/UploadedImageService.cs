namespace XE_Local_AI_Engine.Client.Services.Images;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Accepts, lists and deletes operator-uploaded source images for image editing.
/// </summary>
/// <remarks>
///     An upload is a <c>generated_images</c> row with no job, encrypted at rest by <see cref="IGeneratedImageStore" />.
///     The format is decided by magic bytes and the size by the file header, never by a client-supplied name or MIME
///     type. EXIF orientation is not applied. Singleton: it opens a fresh scope per row operation.
/// </remarks>
public sealed class UploadedImageService
{
    /// <summary>Largest accepted width or height, matching the generation form's clamp.</summary>
    public const int MaxDimension = 2048;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private readonly IGeneratedImageStore _imageStore;
    private readonly IServiceScopeFactory _scopeFactory;

    public UploadedImageService(IGeneratedImageStore imageStore, IServiceScopeFactory scopeFactory)
    {
        _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>Validates and stores one uploaded image.</summary>
    /// <exception cref="ImageUploadRejectedException">The bytes are not an accepted PNG or JPEG image.</exception>
    public Task<GeneratedImageInfo> AddAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var metadata = Inspect(bytes.Span);
        return _imageStore.AddUploadAsync(bytes, metadata, cancellationToken);
    }

    /// <summary>One page of uploaded images, newest first, plus how many exist in total.</summary>
    public async Task<UploadedImagePage> ListAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var rows = scope.ServiceProvider.GetRequiredService<IGeneratedImageRowStore>();
        var items = await rows.ListUploadsAsync(limit, offset, cancellationToken);
        var total = await rows.CountUploadsAsync(cancellationToken);
        return new UploadedImagePage
        {
            Items = items,
            TotalCount = total
        };
    }

    /// <summary>
    ///     Deletes an uploaded image (row first, then blob). <see langword="false" /> when no upload has that id,
    ///     including when the id names a job-produced image.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid imageId, CancellationToken cancellationToken)
    {
        string? storagePath;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var rows = scope.ServiceProvider.GetRequiredService<IGeneratedImageRowStore>();
            storagePath = await rows.DeleteUploadAsync(imageId, cancellationToken);
        }

        if (storagePath is null)
        {
            return false;
        }

        _imageStore.RemoveUploadBlob(imageId, storagePath);
        return true;
    }

    /// <summary>Sniffs the format and reads the header dimensions, refusing anything the edit path cannot use.</summary>
    internal static GeneratedImageMetadata Inspect(ReadOnlySpan<byte> bytes)
    {
        string mimeType;
        int width;
        int height;
        if (bytes.StartsWith(PngSignature))
        {
            if (PngImageDimensions.TryRead(bytes) is not { } dimensions)
            {
                throw new ImageUploadRejectedException("The image header could not be read.");
            }

            (mimeType, width, height) = ("image/png", dimensions.Width, dimensions.Height);
        }
        else if (bytes.StartsWith(JpegSignature))
        {
            if (!JpegImageDimensions.TryRead(bytes, out width, out height, out var components))
            {
                throw new ImageUploadRejectedException("The image header could not be read.");
            }

            // sd-server decodes 3- or 4-channel input as RGB(A); a 4-component JPEG is CMYK and would come out as garbage colours.
            if (components == 4)
            {
                throw new ImageUploadRejectedException("CMYK JPEG images are not supported. Save the image as RGB and upload it again.");
            }

            mimeType = "image/jpeg";
        }
        else
        {
            throw new ImageUploadRejectedException("Only PNG and JPEG images can be uploaded.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ImageUploadRejectedException("The image header could not be read.");
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            throw new ImageUploadRejectedException($"Images larger than {MaxDimension} pixels on either side are not supported.");
        }

        return new GeneratedImageMetadata
        {
            Width = width,
            Height = height,
            MimeType = mimeType
        };
    }
}
