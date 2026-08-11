namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Outcome of extracting an uploaded document to Markdown/plaintext. Persisted as the enum name on the
///     <c>conversation_uploaded_files.extraction_status</c> column and surfaced to the chat/agent surfaces. This is the
///     canonical definition owned by the persistence lane; the extraction service produces these values.
/// </summary>
public enum DocumentExtractionStatus
{
    /// <summary>Extraction has not run yet (row created before the extractor completed).</summary>
    Pending,

    /// <summary>Extraction succeeded; the cached Markdown is available on disk.</summary>
    Extracted,

    /// <summary>The file type is not supported by any pure-.NET reader (for example an image or unknown extension).</summary>
    Unsupported,

    /// <summary>A supported type was attempted but extraction threw or produced nothing usable.</summary>
    Failed,

    /// <summary>
    ///     An image accepted for direct vision (multimodal) input. The raw bytes are stored encrypted; no text extraction
    ///     runs and no Markdown is cached (the bytes ride the turn as an image part when the model is vision-capable).
    /// </summary>
    Image
}
