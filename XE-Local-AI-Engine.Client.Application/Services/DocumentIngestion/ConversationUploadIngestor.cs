namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Default <see cref="IConversationUploadIngestor" />. Holds the gate/extract/persist orchestration that used to live
///     in the upload endpoint handler. Stateless apart from its (singleton) collaborators.
/// </summary>
public sealed class ConversationUploadIngestor(
    IConversationUploadedFileStore fileStore,
    IDocumentTextExtractor extractor,
    IDocumentExtractionAdmissionGate extractionGate) : IConversationUploadIngestor
{
    private const string DefaultMimeType = "application/octet-stream";

    // Image types accepted for direct vision (multimodal) input: their bytes are stored as-is with no text extraction.
    // Whether a given turn's model can actually see them is gated later (ChatTurnResolution.SupportsVision); a non-vision
    // model silently omits them. This map is the admission allowlist AND the CANONICAL media type: the stored MimeType is
    // derived from the extension, never the client-supplied multipart Content-Type (which can be blank, generic, or
    // spoofed) — that value becomes DataContent.MediaType and must start with image/ for the provider + token estimators.
    private static readonly Dictionary<string, string> ImageMediaTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private readonly IDocumentExtractionAdmissionGate _extractionGate = extractionGate ?? throw new ArgumentNullException(nameof(extractionGate));
    private readonly IDocumentTextExtractor _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    private readonly IConversationUploadedFileStore _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));

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

        // Aggregate admission: bound how many uploads buffer + extract + persist concurrently so a burst cannot
        // aggregate to OOM. By the time this runs ASP.NET has already buffered the multipart body (largely disk-spooled),
        // so the gate does not bound that framework buffer; it bounds the memory-heavy phase this method owns — the
        // in-memory byte[] copy of the file, the extraction, and the encrypted persistence write. Holding the lease
        // through persistence keeps the raw-bytes + encrypted-copy phase inside the admitted count. When the gate is
        // full, fail fast so the caller can answer busy rather than piling up in-flight byte[] copies.
        if (!_extractionGate.TryAcquire(out var extractionLease))
        {
            return null;
        }

        using (extractionLease)
        {
            var bytes = await ReadAllBytesAsync(content, cancellationToken).ConfigureAwait(false);

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
                var extraction = await _extractor.ExtractAsync(extractionStream, originalFileName, extension, cancellationToken).ConfigureAwait(false);
                status = extraction.Status;
                markdown = extraction.Markdown;
                extractedChars = extraction.ExtractedChars;
            }

            // An admitted image's media type is the canonical value for its extension — never the client-supplied
            // Content-Type — so DataContent.MediaType is always a correct image/* type. Non-image files keep the
            // client type (with the octet-stream fallback) since the extractor path does not depend on it.
            string mimeType;
            if (isImage)
            {
                mimeType = canonicalImageMediaType!;
            }
            else
            {
                mimeType = string.IsNullOrWhiteSpace(clientContentType) ? DefaultMimeType : clientContentType;
            }

            var input = new ConversationUploadedFileInput(conversationId,
                Guid.NewGuid(),
                originalFileName,
                mimeType,
                extension,
                bytes.Length,
                bytes,
                status,
                markdown,
                extractedChars);

            return await _fileStore.AddAsync(input, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
