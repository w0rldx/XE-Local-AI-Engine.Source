namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DataIngestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
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
        var html = new HtmlDocumentReader();
        var pdf = new PdfDocumentReader();
        var docx = new DocxDocumentReader();

        var readers = new Dictionary<string, IngestionDocumentReader>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in PlaintextDocumentReader.SupportedExtensions)
        {
            readers[extension] = plaintext;
        }

        readers[".html"] = html;
        readers[".htm"] = html;
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

    private async Task<ExtractedDocument> ReadStructuredAsync(IngestionDocumentReader reader,
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
        return new ExtractedDocument(document, inputBytes);
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

    // Two-stage ZIP preflight, both stages reading only the central directory with no decompression. The first stage
    // reads the declared total-entry count straight from the End Of Central Directory record, WITHOUT constructing a
    // ZipArchive, and rejects an over-count archive before .NET materializes a ZipArchiveEntry for every member.
    // Touching ZipArchive.Entries reads the whole central directory and allocates one entry object per member up front,
    // so a metadata-heavy archive would otherwise allocate hundreds of MB before an in-loop counter could run; the EOCD
    // check bounds that with zero entry allocations. Only once the declared count is within the ceiling, so
    // materialization is bounded, does the second stage open ZipArchive and sum the declared sizes with overflow-safe
    // arithmetic to reject an oversize or oversized-ratio archive. A stream that is not a readable zip is NOT rejected
    // here: it falls through (return null) so the reader surfaces its own error, rather than duplicating error semantics.
    private string? EvaluateZipPreflight(MemoryStream buffer)
    {
        // Stage 1: declared entry count from the EOCD record — zero ZipArchiveEntry allocations. A null result means no
        // well-formed EOCD was found in the bounded search window (a malformed/non-zip stream): fall through so the
        // reader surfaces its own error, matching the old InvalidDataException path.
        var declaredEntryCount = TryReadDeclaredZipEntryCount(buffer);
        if (declaredEntryCount is null)
        {
            return null;
        }

        // A negative Zip64 count is hostile metadata (high bit set when read as signed); reject it like an over-count.
        if (declaredEntryCount < 0 || declaredEntryCount > _maxCompressedEntries)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Compressed document declares more than {_maxCompressedEntries} entries, which exceeds the extraction limit.");
        }

        // Stage 2: size/ratio pass. Entry materialization is now bounded by the count check above (at most ceiling-count
        // entry metadata objects). Declared before the try / disposed in the finally so the archive is released on every
        // path (CA2000). A stream that passed the EOCD check but is not otherwise a readable zip throws
        // InvalidDataException; we swallow it and return null so the reader surfaces its own error.
        ZipArchive? archive = null;
        try
        {
            // leaveOpen: the reader re-parses this same buffer afterward. Read mode reads only the central directory.
            archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);

            // Length is the declared uncompressed size from the central directory (cheap, and may be a lie — the
            // post-parse guards are the backstop). CompressedLength is the on-disk size of the same entry. Both are
            // aggregated overflow-safely in the seam below.
            return EvaluateDeclaredZipSizes(archive.Entries.Select(static entry => new ZipEntrySize(entry.Length, entry.CompressedLength)),
                _maxDeclaredUncompressedBytes,
                _maxCompressionRatio);
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

    // Overflow- and hostile-metadata-safe aggregation of a ZIP's declared central-directory sizes. Kept as a static
    // seam (internal + InternalsVisibleTo) so the Zip64 overflow/negative-size rejection paths can be exercised without
    // forging a binary archive that a real ZipArchive would parse. Returns a content-free rejection reason, or null when
    // the declared totals are within bounds.
    internal static string? EvaluateDeclaredZipSizes(IEnumerable<ZipEntrySize> entries,
        long maxDeclaredUncompressedBytes,
        int maxCompressionRatio)
    {
        long declaredUncompressed = 0;
        long compressed = 0;

        foreach (var (length, compressedLength) in entries)
        {
            // Hostile Zip64 central-directory values can be negative (high bit set when read as signed); a negative term
            // would pull the running total down and slip an oversize archive past the absolute ceiling. Reject instead.
            if (length < 0 || compressedLength < 0)
            {
                return "Compressed document declares an invalid entry size, which exceeds the extraction limit.";
            }

            // Saturating adds: many individually-valid Zip64 sizes can sum past long.MaxValue and wrap to a small or
            // negative total that would bypass the ceilings below. Clamp at long.MaxValue so an overflowing total
            // always REJECTS rather than wrapping.
            declaredUncompressed = SaturatingAdd(declaredUncompressed, length);
            compressed = SaturatingAdd(compressed, compressedLength);
        }

        if (declaredUncompressed > maxDeclaredUncompressedBytes)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Compressed document declares {declaredUncompressed / (1024 * 1024)} MB uncompressed, which exceeds the {maxDeclaredUncompressedBytes / (1024 * 1024)} MB extraction limit.");
        }

        // Ratio guard, overflow-safe: reject when declared > compressed * ratio, but never evaluate the product when it
        // would overflow long. When it would overflow, the threshold exceeds any representable declared total, so the
        // ratio cannot be exceeded — the absolute ceiling above is the operative guard for such extreme compressed sizes
        // (physically impossible within the bounded input buffer, reachable only via lying metadata that the absolute
        // ceiling already caught).
        if (compressed > 0 && maxCompressionRatio > 0 && compressed <= long.MaxValue / maxCompressionRatio)
        {
            var threshold = compressed * maxCompressionRatio;
            if (declaredUncompressed > threshold)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Compressed document declares a {declaredUncompressed / compressed}x expansion ratio, which exceeds the allowed {maxCompressionRatio}x extraction limit.");
            }
        }

        return null;
    }

    // Saturating add for non-negative addends: returns long.MaxValue instead of wrapping on overflow.
    private static long SaturatingAdd(long current, long addend)
    {
        return current > long.MaxValue - addend ? long.MaxValue : current + addend;
    }

    // Reads the declared total-entry count from a ZIP's End Of Central Directory record — and, when that classic field
    // is saturated (0xFFFF), from the Zip64 EOCD locator + record it points to — over the already-buffered bytes and
    // WITHOUT constructing a ZipArchive (so no ZipArchiveEntry is allocated). Returns the declared count, or null when
    // no well-formed EOCD is found within the bounded search window (a malformed/non-zip stream), so the caller falls
    // through to the reader's own error handling. Strictly bounded: never scans outside the trailing window, and any
    // structural inconsistency yields null rather than a throw.
    private static long? TryReadDeclaredZipEntryCount(MemoryStream buffer)
    {
        const uint EocdSignature = 0x06054b50;
        const uint Zip64LocatorSignature = 0x07064b50;
        const uint Zip64EocdSignature = 0x06064b50;
        const int EocdMinSize = 22; // fixed EOCD record size, excluding the trailing comment
        const int MaxCommentSize = 0xFFFF; // the EOCD comment length is a ushort, so the comment is at most 65,535 bytes
        const int Zip64LocatorSize = 20;

        var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (data.Length < EocdMinSize)
        {
            return null;
        }

        // The EOCD lies within the last (EocdMinSize + max-comment) bytes; scan backward for its signature, bounded to
        // that window so a hostile stream can never make us scan the whole buffer.
        var searchFloor = Math.Max(0, data.Length - (EocdMinSize + MaxCommentSize));
        var eocd = -1;
        for (var i = data.Length - EocdMinSize; i >= searchFloor; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)) != EocdSignature)
            {
                continue;
            }

            // Confirm the record's declared comment length lands exactly at the stream end, so we do not latch onto
            // EOCD-signature-looking bytes inside the archive payload.
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i + 20, 2));
            if (i + EocdMinSize + commentLength == data.Length)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            return null;
        }

        // Total entries: 2-byte field at offset 10. 0xFFFF signals the real count lives in the Zip64 records.
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(eocd + 10, 2));
        if (totalEntries != 0xFFFF)
        {
            return totalEntries;
        }

        // The Zip64 EOCD locator sits immediately before the EOCD.
        var locator = eocd - Zip64LocatorSize;
        if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(locator, 4)) != Zip64LocatorSignature)
        {
            return null;
        }

        // Locator offset 8: 8-byte relative offset of the Zip64 EOCD record. Bounds-check before reading the record's
        // 8-byte total-entries field at record offset 32 (so we touch bytes [offset, offset + 40)).
        var zip64EocdOffset = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(locator + 8, 8));
        if (zip64EocdOffset < 0 || zip64EocdOffset + 40 > data.Length)
        {
            return null;
        }

        var recordStart = (int)zip64EocdOffset;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(recordStart, 4)) != Zip64EocdSignature)
        {
            return null;
        }

        // Zip64 EOCD record offset 32: 8-byte total number of central-directory entries. A negative value here is
        // hostile metadata; the caller rejects it the same as an over-count.
        return BinaryPrimitives.ReadInt64LittleEndian(data.Slice(recordStart + 32, 8));
    }

    // Content-free reason surfaced when the PDF preflight's own PdfDocument.Open fails to parse the document. It stands
    // in for the reader's generic failure so a malformed PDF is NOT handed to the reader for a second full parse.
    private const string PdfUnparseableReason = "The document could not be parsed.";

    // Opens the PDF far enough to read its declared page count (PdfPig reads the xref/catalog, not the per-page content
    // streams) and rejects when it exceeds the page ceiling — before the expensive per-page text extraction runs. The
    // open runs against a non-owning VIEW over the buffered bytes (no copy) so PdfPig seeking/closing its stream never
    // disturbs the shared buffer the reader re-parses afterward.
    //
    // Honest residual: PdfDocument.Open itself IS parser work (it reads the cross-reference/catalog), so the open cost
    // precedes the page cap — the cap bounds per-page text EXTRACTION, not the document-open cost. Contract is
    // "narrow + no-reparse": we catch ONLY PdfPig's own format/document exceptions (a malformed document), never
    // resource failures such as OutOfMemoryException, which must propagate. On a format failure we do NOT return null
    // (which would let the reader re-open the same bytes for a wasted second parse); instead we return a content-free
    // reason so the caller fails the document up front. On success — a healthy, within-cap document — we return null and
    // the reader performs its own parse; that second parse is the accepted cost of a healthy document.
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
            catch (Exception exception) when (exception is PdfDocumentFormatException
                                                  or PdfDocumentEncryptedException
                                                  or PdfDocumentStackDepthException)
            {
                return PdfUnparseableReason;
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

    // One ZIP central-directory entry's declared sizes. Both may be hostile: negative (a high-bit-set Zip64 value read
    // as signed) or summing past long.MaxValue, which is why the aggregation saturates rather than wrapping.
    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct ZipEntrySize(long Length, long CompressedLength);

    // A parsed structured document plus the raw byte count buffered to produce it (the expansion-ratio denominator).
    private sealed record ExtractedDocument(IngestionDocument Document, long InputBytes);
}
