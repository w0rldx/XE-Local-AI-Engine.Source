namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Globalization;
using Microsoft.Extensions.DataIngestion;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

/// <summary>
///     Pure-managed document text extractor. Dispatches an uploaded file to the reader for its extension, flattens
///     the resulting <see cref="IngestionDocument"/> to Markdown/plaintext, and caps the output to bound memory.
///     Stateless and thread-safe — safe to register as a singleton.
/// </summary>
public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    private readonly IReadOnlyDictionary<string, IngestionDocumentReader> _readersByExtension;
    private readonly int _maxOutputChars;
    private readonly ILogger<DocumentTextExtractor> _logger;

    public DocumentTextExtractor(ILogger<DocumentTextExtractor> logger)
        : this(logger, DocumentExtractionLimits.DefaultMaxOutputChars)
    {
    }

    // Char cap is overridable so tests can exercise truncation without allocating multi-megabyte inputs.
    internal DocumentTextExtractor(ILogger<DocumentTextExtractor> logger, int maxOutputChars)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _maxOutputChars = maxOutputChars;

        var plaintext = new PlaintextDocumentReader();
        var pdf = new PdfDocumentReader();
        var docx = new DocxDocumentReader();

        var readers = new Dictionary<string, IngestionDocumentReader>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in PlaintextDocumentReader.SupportedExtensions)
        {
            readers[extension] = plaintext;
        }

        readers[".pdf"] = pdf;
        readers[".docx"] = docx;

        _readersByExtension = readers;
    }

    public bool IsSupported(string extension)
    {
        return _readersByExtension.ContainsKey(NormalizeExtension(extension));
    }

    public async Task<DocumentExtractionResult> ExtractAsync(Stream content, string fileName, string extension, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedExtension = NormalizeExtension(extension);
        if (!_readersByExtension.TryGetValue(normalizedExtension, out var reader))
        {
            return new DocumentExtractionResult(DocumentExtractionStatus.Unsupported, Markdown: null, ExtractedChars: null, Error: null);
        }

        try
        {
            var document = await ReadStructuredAsync(reader, content, fileName, normalizedExtension, cancellationToken).ConfigureAwait(false);
            var markdown = Truncate(IngestionDocumentMarkdownSerializer.Serialize(document));

            return new DocumentExtractionResult(DocumentExtractionStatus.Extracted, markdown, markdown.Length, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never log file content or the file name: the extension and the exception type are enough to triage.
            _logger.LogWarning("Document extraction failed for a {Extension} upload ({ExceptionType}).", normalizedExtension, exception.GetType().Name);
            return new DocumentExtractionResult(DocumentExtractionStatus.Failed,
                Markdown: null,
                ExtractedChars: null,
                Error: string.Create(CultureInfo.InvariantCulture, $"Extraction failed ({exception.GetType().Name})."));
        }
    }

    public async Task<DocumentStructuredExtractionResult> ExtractStructuredAsync(Stream content, string fileName, string extension, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedExtension = NormalizeExtension(extension);
        if (!_readersByExtension.TryGetValue(normalizedExtension, out var reader))
        {
            return new DocumentStructuredExtractionResult(DocumentExtractionStatus.Unsupported, Document: null, Error: null);
        }

        try
        {
            // Return the reader's structured document verbatim (no Markdown flattening or char cap): the chunking lane
            // needs the heading structure, and it applies its own per-chunk size bound.
            var document = await ReadStructuredAsync(reader, content, fileName, normalizedExtension, cancellationToken).ConfigureAwait(false);
            return new DocumentStructuredExtractionResult(DocumentExtractionStatus.Extracted, document, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never log file content or the file name: the extension and the exception type are enough to triage.
            _logger.LogWarning("Structured document extraction failed for a {Extension} upload ({ExceptionType}).", normalizedExtension, exception.GetType().Name);
            return new DocumentStructuredExtractionResult(DocumentExtractionStatus.Failed,
                Document: null,
                Error: string.Create(CultureInfo.InvariantCulture, $"Extraction failed ({exception.GetType().Name})."));
        }
    }

    private static async Task<IngestionDocument> ReadStructuredAsync(IngestionDocumentReader reader,
        Stream content,
        string fileName,
        string normalizedExtension,
        CancellationToken cancellationToken)
    {
        // Buffer to a seekable stream: PdfPig and the Open XML SDK both seek, and an upload stream may be
        // forward-only. The endpoint caps the upload size, so the buffered input is bounded.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        return await reader.ReadAsync(buffer, fileName, normalizedExtension, cancellationToken).ConfigureAwait(false);
    }

    private string Truncate(string markdown)
    {
        return markdown.Length <= _maxOutputChars ? markdown : markdown[.._maxOutputChars];
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        // The reader dictionary uses StringComparer.OrdinalIgnoreCase, so casing is handled by lookup — only the
        // leading dot needs to be normalized here.
        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }
}
