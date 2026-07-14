namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Buffers;
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
    private readonly int _maxStructuredOutputChars;
    private readonly int _maxExpansionRatio;
    private readonly int _minCharsForExpansionGuard;
    private readonly ILogger<DocumentTextExtractor> _logger;

    public DocumentTextExtractor(ILogger<DocumentTextExtractor> logger)
        : this(logger,
            DocumentExtractionLimits.DefaultMaxOutputChars,
            DocumentExtractionLimits.DefaultMaxStructuredOutputChars,
            DocumentExtractionLimits.DefaultMaxExpansionRatio,
            DocumentExtractionLimits.MinCharsForExpansionGuard)
    {
    }

    // Caps are overridable so tests can exercise truncation / rejection without allocating multi-megabyte inputs.
    internal DocumentTextExtractor(ILogger<DocumentTextExtractor> logger,
        int maxOutputChars,
        int maxStructuredOutputChars,
        int maxExpansionRatio,
        int minCharsForExpansionGuard)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _maxOutputChars = maxOutputChars;
        _maxStructuredOutputChars = maxStructuredOutputChars;
        _maxExpansionRatio = maxExpansionRatio;
        _minCharsForExpansionGuard = minCharsForExpansionGuard;

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
            var (document, inputBytes) = await ReadStructuredAsync(reader, content, fileName, normalizedExtension, cancellationToken).ConfigureAwait(false);
            var serialized = IngestionDocumentMarkdownSerializer.Serialize(document);

            // Bomb guard: reject a small upload that expanded into a huge text body (bytes-out vs bytes-in ratio) before
            // truncation would mask it. The absolute char cap below still bounds a legitimately large document.
            var expansionReason = EvaluateExpansion(inputBytes, serialized.Length);
            if (expansionReason is not null)
            {
                _logger.LogWarning("Document extraction rejected a {Extension} upload: {Reason}", normalizedExtension, expansionReason);
                return new DocumentExtractionResult(DocumentExtractionStatus.Failed, Markdown: null, ExtractedChars: null, expansionReason);
            }

            var markdown = Truncate(serialized);
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
            // Return the reader's structured document verbatim (the chunking lane needs the heading structure and
            // applies its own per-chunk size bound), but first bound its AGGREGATE size: the verbatim path had no
            // ceiling, so a huge or bomb-expanded document reached chunking/persistence unbounded.
            var (document, inputBytes) = await ReadStructuredAsync(reader, content, fileName, normalizedExtension, cancellationToken).ConfigureAwait(false);

            var totalChars = SumContentChars(document);
            var boundsReason = EvaluateStructuredBounds(inputBytes, totalChars);
            if (boundsReason is not null)
            {
                _logger.LogWarning("Structured document extraction rejected a {Extension} upload: {Reason}", normalizedExtension, boundsReason);
                return new DocumentStructuredExtractionResult(DocumentExtractionStatus.Failed, Document: null, boundsReason);
            }

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

    private static async Task<(IngestionDocument Document, long InputBytes)> ReadStructuredAsync(IngestionDocumentReader reader,
        Stream content,
        string fileName,
        string normalizedExtension,
        CancellationToken cancellationToken)
    {
        // Buffer to a seekable stream: PdfPig and the Open XML SDK both seek, and an upload stream may be
        // forward-only. The endpoint caps the upload size, but a container format (.docx zip / .pdf) can expand well
        // beyond its on-disk size, so buffering is bounded here by MaxDecompressedBytes as a decompression-bomb ceiling:
        // exceeding it throws (surfaced as a content-free Failed result by the caller) instead of risking OOM.
        // Residual risk: this bounds the bytes we materialize into the buffer; the reader still decompresses the zip/pdf
        // internally, so a container that stays under the ceiling but expands hugely inside the SDK is not fully bounded
        // by this guard alone — the caller's output expansion-ratio guard catches that on the way out.
        using var buffer = new MemoryStream();
        var inputBytes = await CopyWithCeilingAsync(content, buffer, DocumentExtractionLimits.MaxDecompressedBytes, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        var document = await reader.ReadAsync(buffer, fileName, normalizedExtension, cancellationToken).ConfigureAwait(false);
        return (document, inputBytes);
    }

    // Copies source into destination, failing fast once more than maxBytes have been read so a bomb upload cannot
    // materialize an unbounded buffer. Returns the total bytes copied. Throws InvalidDataException (content-free) when
    // the ceiling is exceeded.
    private static async Task<long> CopyWithCeilingAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new InvalidDataException("Document exceeds the maximum decompressed size allowed for extraction.");
                }

                await destination.WriteAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // Total character count across the structured document's content elements — the aggregate we bound before handing
    // the verbatim document to chunking.
    private static long SumContentChars(IngestionDocument document)
    {
        long total = 0;
        foreach (var element in document.EnumerateContent())
        {
            total += element.Text?.Length ?? 0;
        }

        return total;
    }

    // Aggregate bound for the structured path: the absolute output-char cap first, then the expansion-ratio guard.
    // Returns a content-free failure reason, or null when the output is within bounds.
    private string? EvaluateStructuredBounds(long inputBytes, long outputChars)
    {
        if (outputChars > _maxStructuredOutputChars)
        {
            return "Document text exceeds the maximum extractable size.";
        }

        return EvaluateExpansion(inputBytes, outputChars);
    }

    // Expansion-ratio guard: fires only once the output is already large (above the floor) AND it exceeds the allowed
    // multiple of the input bytes — the signature of a small container that expanded into a huge text body. Returns a
    // content-free failure reason, or null.
    private string? EvaluateExpansion(long inputBytes, long outputChars)
    {
        if (outputChars < _minCharsForExpansionGuard)
        {
            return null;
        }

        if (inputBytes > 0 && outputChars > inputBytes * (long)_maxExpansionRatio)
        {
            return "Document expands beyond the allowed size ratio during extraction.";
        }

        return null;
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
