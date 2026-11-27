namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Implementation;

using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Turns one admitted conversation upload into a persisted file: takes the extraction admission lease, buffers the
///     bytes, runs text extraction or skips it for a vision image, resolves the trusted MIME type, and persists through
///     <see cref="IConversationUploadedFileStore" />.
/// </summary>
/// <remarks>
///     All of that happens inside the lease. This type owns the accepted-image allowlist, so the upload endpoint only
///     validates the request and maps the result. Stateless apart from its (singleton) collaborators.
/// </remarks>
public sealed class ConversationUploadIngestor
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

    private readonly DocumentExtractionAdmissionGate _extractionGate;
    private readonly IDocumentTextExtractor _extractor;
    private readonly IConversationUploadedFileStore _fileStore;
    private readonly INodeChatMutationGuard _mutationGuard;

    public ConversationUploadIngestor(IConversationUploadedFileStore fileStore,
        IDocumentTextExtractor extractor,
        DocumentExtractionAdmissionGate extractionGate,
        INodeChatMutationGuard mutationGuard)
    {
        ArgumentNullException.ThrowIfNull(mutationGuard);
        _mutationGuard = mutationGuard;
        ArgumentNullException.ThrowIfNull(extractionGate);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(fileStore);
        _extractionGate = extractionGate;
        _extractor = extractor;
        _fileStore = fileStore;
    }

    /// <summary>
    ///     Whether an upload with this extension is accepted at all: either an image admitted for direct vision input or
    ///     a type the text extractor supports.
    /// </summary>
    public bool IsSupportedExtension(string extension)
    {
        return ImageMediaTypesByExtension.ContainsKey(extension) || _extractor.IsSupported(extension);
    }

    /// <summary>Ingests and persists one uploaded file, returning its stored metadata.</summary>
    /// <remarks>
    ///     Returns <see langword="null" /> when the extraction admission gate is at capacity; the caller must then reject
    ///     with a retryable busy response rather than piling up in-flight byte copies.
    /// </remarks>
    public async Task<ConversationUploadedFileInfo?> IngestAsync(Guid conversationId,
        Stream content,
        string originalFileName,
        string extension,
        string? clientContentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Refused before the admission gate or any byte is read: an attachment is a mutation of the conversation.
        await _mutationGuard.EnsureMutableAsync(conversationId, cancellationToken);

        var isImage = ImageMediaTypesByExtension.TryGetValue(extension, out var canonicalImageMediaType);

        // Aggregate admission bounding the memory-heavy phase this method owns — in-memory copy, extraction, encrypted
        // write — so a burst cannot reach OOM; the lease is held through persistence and a full gate fails fast as busy.
        var admitted = _extractionGate.TryAcquire(out var extractionLease);

        using (extractionLease)
        {
            if (!admitted)
            {
                return null;
            }

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
