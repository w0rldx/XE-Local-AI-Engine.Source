namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Turns one admitted conversation upload into a persisted file: takes the extraction admission lease, buffers the
///     bytes, runs text extraction (or skips it for a vision image), resolves the trusted MIME type, and persists through
///     <see cref="IConversationUploadedFileStore" /> — all inside the lease. Owns the accepted-image allowlist, so the
///     upload endpoint only validates the request and maps the result.
/// </summary>
public interface IConversationUploadIngestor
{
    /// <summary>
    ///     Whether an upload with this extension is accepted at all: either an image admitted for direct vision input or
    ///     a type the text extractor supports.
    /// </summary>
    bool IsSupportedExtension(string extension);

    /// <summary>
    ///     Ingests and persists one uploaded file, returning its stored metadata. Returns <see langword="null" /> when the
    ///     extraction admission gate is at capacity — the caller must then reject with a retryable busy response rather
    ///     than piling up in-flight byte[] copies.
    /// </summary>
    Task<ConversationUploadedFileInfo?> IngestAsync(Guid conversationId,
        Stream content,
        string originalFileName,
        string extension,
        string? clientContentType,
        CancellationToken cancellationToken);
}
