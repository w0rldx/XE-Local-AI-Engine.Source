namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.DataIngestion;
using UglyToad.PdfPig;
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
    private readonly int _maxCompressedEntries;
    private readonly long _maxDeclaredUncompressedBytes;
    private readonly int _maxCompressionRatio;
    private readonly int _maxPdfPageCount;
    private readonly ILogger<DocumentTextExtractor> _logger;

    public DocumentTextExtractor(ILogger<DocumentTextExtractor> logger)
        : this(logger,
            DocumentExtractionLimits.DefaultMaxOutputChars,
            DocumentExtractionLimits.DefaultMaxStructuredOutputChars,
            DocumentExtractionLimits.DefaultMaxExpansionRatio,
            DocumentExtractionLimits.MinCharsForExpansionGuard)
    {
    }

    // Caps are overridable so tests can exercise truncation / rejection without allocating multi-megabyte inputs. The
    // preflight ceilings default to the shared limits and are likewise overridable so a preflight-rejection test can
    // trip them with a small honest archive instead of writing a real gigabyte-scale bomb.
    internal DocumentTextExtractor(ILogger<DocumentTextExtractor> logger,
        int maxOutputChars,
        int maxStructuredOutputChars,
        int maxExpansionRatio,
        int minCharsForExpansionGuard,
        int maxCompressedEntries = DocumentExtractionLimits.DefaultMaxCompressedEntryCount,
        long maxDeclaredUncompressedBytes = DocumentExtractionLimits.DefaultMaxDeclaredUncompressedBytes,
        int maxCompressionRatio = DocumentExtractionLimits.DefaultMaxCompressionRatio,
        int maxPdfPageCount = DocumentExtractionLimits.DefaultMaxPdfPageCount)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _maxOutputChars = maxOutputChars;
        _maxStructuredOutputChars = maxStructuredOutputChars;
        _maxExpansionRatio = maxExpansionRatio;
        _minCharsForExpansionGuard = minCharsForExpansionGuard;
        _maxCompressedEntries = maxCompressedEntries;
        _maxDeclaredUncompressedBytes = maxDeclaredUncompressedBytes;
        _maxCompressionRatio = maxCompressionRatio;
        _maxPdfPageCount = maxPdfPageCount;

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
        catch (DocumentPreflightRejectedException rejected)
        {
            // Rejected up front by the pre-parse preflight; the message is content-free and safe to surface.
            _logger.LogWarning("Document extraction rejected a {Extension} upload at preflight: {Reason}", normalizedExtension, rejected.Message);
            return new DocumentExtractionResult(DocumentExtractionStatus.Failed, Markdown: null, ExtractedChars: null, rejected.Message);
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
        catch (DocumentPreflightRejectedException rejected)
        {
            // Rejected up front by the pre-parse preflight; the message is content-free and safe to surface.
            _logger.LogWarning("Structured document extraction rejected a {Extension} upload at preflight: {Reason}", normalizedExtension, rejected.Message);
            return new DocumentStructuredExtractionResult(DocumentExtractionStatus.Failed, Document: null, rejected.Message);
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

    private async Task<(IngestionDocument Document, long InputBytes)> ReadStructuredAsync(IngestionDocumentReader reader,
        Stream content,
        string fileName,
        string normalizedExtension,
        CancellationToken cancellationToken)
    {
        // Buffer to a seekable stream: PdfPig and the Open XML SDK both seek, and an upload stream may be
        // forward-only. The endpoint caps the upload size; as a second bound the RAW bytes copied here are capped at
        // MaxBufferedInputBytes, and exceeding it throws (surfaced as a content-free Failed result by the caller) instead
        // of risking OOM. This bounds only the raw bytes we materialize into the buffer.
        using var buffer = new MemoryStream();
        var inputBytes = await CopyWithCeilingAsync(content, buffer, DocumentExtractionLimits.MaxBufferedInputBytes, cancellationToken).ConfigureAwait(false);

        // Pre-parse preflight: for a compressed container (zip-based .docx, or a PDF), reject up front using ONLY the
        // container's own cheap metadata — the zip central directory or the PDF page count — BEFORE the reader
        // decompresses/materializes it. Without this, an admitted small container could expand to exhaust memory inside
        // the parser and be caught only AFTER materialization. A hostile central directory can LIE about declared sizes,
        // so this honest-header check is only the first layer: the post-parse output-char cap and expansion-ratio guard
        // in the callers remain the backstop that measures the ACTUAL expanded output (defense in depth).
        buffer.Position = 0;
        var preflightReason = EvaluatePreflight(normalizedExtension, buffer);
        if (preflightReason is not null)
        {
            throw new DocumentPreflightRejectedException(preflightReason);
        }

        buffer.Position = 0;
        var document = await reader.ReadAsync(buffer, fileName, normalizedExtension, cancellationToken).ConfigureAwait(false);
        return (document, inputBytes);
    }

    // Dispatches the cheap pre-parse bounds check by format. Only compressed containers carry metadata worth
    // inspecting, so plaintext yields no reason. Returns a content-free rejection reason, or null to proceed to the parser.
    private string? EvaluatePreflight(string normalizedExtension, MemoryStream buffer)
    {
        return normalizedExtension switch
        {
            ".docx" => EvaluateZipPreflight(buffer),
            ".pdf" => EvaluatePdfPreflight(buffer),
            _ => null
        };
    }

    // Reads the ZIP central directory (no decompression) and rejects an archive whose declared entry count, summed
    // declared-uncompressed size, or declared expansion ratio exceeds the ceilings. A stream that is not a readable zip
    // is NOT rejected here: it falls through so the reader surfaces its own error, rather than duplicating error
    // semantics. Only a successfully-read central directory that violates a ceiling is rejected.
    private string? EvaluateZipPreflight(MemoryStream buffer)
    {
        // Declared before the try / disposed in the finally so the archive is released on every path (CA2000). A stream
        // that is not a readable zip throws InvalidDataException from the ctor or the central-directory read; we swallow
        // it and return null so the reader surfaces its own error instead of us duplicating the failure semantics.
        ZipArchive? archive = null;
        try
        {
            // leaveOpen: the reader re-parses this same buffer afterward. Read mode reads only the central directory.
            archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);

            long declaredUncompressed = 0;
            long compressed = 0;
            var entryCount = 0;

            foreach (var entry in archive.Entries)
            {
                entryCount++;
                if (entryCount > _maxCompressedEntries)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"Compressed document declares more than {_maxCompressedEntries} entries, which exceeds the extraction limit.");
                }

                // Length is the declared uncompressed size from the central directory (cheap, and may be a lie — the
                // post-parse guards are the backstop). CompressedLength is the on-disk size of the same entry.
                declaredUncompressed += entry.Length;
                compressed += entry.CompressedLength;
            }

            if (declaredUncompressed > _maxDeclaredUncompressedBytes)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Compressed document declares {declaredUncompressed / (1024 * 1024)} MB uncompressed, which exceeds the {_maxDeclaredUncompressedBytes / (1024 * 1024)} MB extraction limit.");
            }

            if (compressed > 0 && declaredUncompressed > compressed * (long)_maxCompressionRatio)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Compressed document declares a {declaredUncompressed / compressed}x expansion ratio, which exceeds the allowed {_maxCompressionRatio}x extraction limit.");
            }

            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        finally
        {
            archive?.Dispose();
        }
    }

    // Opens the PDF far enough to read its declared page count (PdfPig reads the xref/catalog, not the per-page content
    // streams) and rejects when it exceeds the page ceiling — before the expensive per-page text extraction runs. A
    // malformed PDF is NOT rejected here: it falls through so the reader surfaces its own error. The open runs against a
    // non-owning VIEW over the buffered bytes (no copy) so PdfPig seeking/closing its stream never disturbs the shared
    // buffer the reader re-parses afterward.
    private string? EvaluatePdfPreflight(MemoryStream buffer)
    {
        int pageCount;
        using (var view = new MemoryStream(buffer.GetBuffer(), 0, (int)buffer.Length, writable: false, publiclyVisible: false))
        {
            try
            {
                using var pdf = PdfDocument.Open(view);
                pageCount = pdf.NumberOfPages;
            }
            catch (Exception)
            {
                return null;
            }
        }

        if (pageCount > _maxPdfPageCount)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"PDF declares {pageCount} pages, which exceeds the {_maxPdfPageCount}-page extraction limit.");
        }

        return null;
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
                    throw new InvalidDataException("Document exceeds the maximum buffered input size allowed for extraction.");
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
