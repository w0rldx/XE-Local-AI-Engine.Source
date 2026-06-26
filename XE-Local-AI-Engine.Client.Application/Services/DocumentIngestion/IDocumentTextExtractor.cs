namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Dispatches an uploaded document's bytes to a pure-managed reader and returns extracted Markdown/plaintext.
///     Implementations are stateless and thread-safe, so they are safe to resolve as a singleton.
/// </summary>
public interface IDocumentTextExtractor
{
    /// <summary>
    ///     Whether the given file extension has a v1 reader. The extension is matched case-insensitively and a
    ///     leading dot is optional (both <c>".pdf"</c> and <c>"pdf"</c> resolve identically).
    /// </summary>
    bool IsSupported(string extension);

    /// <summary>
    ///     Extracts text from <paramref name="content"/>. This never throws for malformed input: a corrupt or
    ///     unreadable file yields <see cref="DocumentExtractionStatus.Failed"/> and an unknown extension yields
    ///     <see cref="DocumentExtractionStatus.Unsupported"/>. The output is capped to bound memory.
    /// </summary>
    /// <param name="content">The uploaded file bytes. The stream is consumed but not disposed by this method.</param>
    /// <param name="fileName">The original file name, used only as the document identifier (never logged).</param>
    /// <param name="extension">The file extension that selects the reader (case-insensitive, leading dot optional).</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    Task<DocumentExtractionResult> ExtractAsync(Stream content, string fileName, string extension, CancellationToken cancellationToken);
}
