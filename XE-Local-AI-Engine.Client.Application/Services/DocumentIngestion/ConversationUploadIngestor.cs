namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Default <see cref="IConversationUploadIngestor" />. Holds the gate/extract/persist orchestration that used to live
///     in the upload endpoint handler. Stateless apart from its (singleton) collaborators.
/// </summary>
public sealed class ConversationUploadIngestor : IConversationUploadIngestor
{
    private const string DefaultMimeType = "application/octet-stream";

    // Images admitted for direct vision: bytes stored as-is, visibility gated later by ChatTurnResolution.SupportsVision.
    // Also the canonical DataContent.MediaType, which must be an image type, so it comes from the extension, never the spoofable client type.
    private static readonly Dictionary<string, string> ImageMediaTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private readonly IDocumentExtractionAdmissionGate _extractionGate;
    private readonly IDocumentTextExtractor _extractor;
    private readonly IConversationUploadedFileStore _fileStore;

    public ConversationUploadIngestor(
        IConversationUploadedFileStore fileStore,
        IDocumentTextExtractor extractor,
        IDocumentExtractionAdmissionGate extractionGate)
    {
        ArgumentNullException.ThrowIfNull(extractionGate);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(fileStore);
        _extractionGate = extractionGate;
        _extractor = extractor;
        _fileStore = fileStore;
    }

    public bool IsSupportedExtension(string extension)
    {
        return ImageMediaTypesByExtension.ContainsKey(extension) || _extractor.IsSupported(extension);
    }

    public async Task<ConversationUploadedFileInfo?> IngestAsync(Guid conversationId,
        Stream content,
        string originalFileName,
        string extension,
        string? clientContentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var isImage = ImageMediaTypesByExtension.TryGetValue(extension, out var canonicalImageMediaType);

        // Aggregate admission bounding the memory-heavy phase this method owns — in-memory copy, extraction, encrypted
        // write — so a burst cannot reach OOM; the lease is held through persistence and a full gate fails fast as busy.
        if (!_extractionGate.TryAcquire(out var extractionLease))
        {
            return null;
        }

        using (extractionLease)
        {
            var bytes = await ReadAllBytesAsync(content, cancellationToken);

            // Images skip text extraction entirely: the raw bytes are the payload (persisted encrypted by the store),
            // marked with the Image status and no cached Markdown. Non-image files keep the exact extract-then-persist path.
            DocumentExtractionStatus status;
            string? markdown;
            int? extractedChars;
            if (isImage)
            {
                status = DocumentExtractionStatus.Image;
                markdown = null;
                extractedChars = null;
            }
            else
            {
                using var extractionStream = new MemoryStream(bytes, writable: false);
                var extraction = await _extractor.ExtractAsync(extractionStream, originalFileName, extension, cancellationToken);
                status = extraction.Status;
                markdown = extraction.Markdown;
                extractedChars = extraction.ExtractedChars;
            }

            // An admitted image's media type is the canonical value for its extension, never the client Content-Type, so
            // DataContent.MediaType is always a correct image type; non-image files keep the client type, unused here.
            string mimeType;
            if (isImage)
            {
                mimeType = canonicalImageMediaType!;
            }
            else
            {
                mimeType = string.IsNullOrWhiteSpace(clientContentType) ? DefaultMimeType : clientContentType;
            }

            var input = new ConversationUploadedFileInput
            {
                ConversationId = conversationId,
                FileId = Guid.NewGuid(),
                OriginalFileName = originalFileName,
                MimeType = mimeType,
                Extension = extension,
                SizeBytes = bytes.Length,
                Content = bytes,
                ExtractionStatus = status,
                ExtractedMarkdown = markdown,
                ExtractedChars = extractedChars
            };

            return await _fileStore.AddAsync(input, cancellationToken);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
