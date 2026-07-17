namespace XE_Local_AI_Engine.Tests.DocumentIngestion;

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The pure-.NET document extractor turns uploaded files into Markdown/plaintext for the chat/agent surfaces.
///     These tests cover the v1-core reader tiers (plaintext with legacy-encoding detection, PDF, and DOCX) and the
///     no-throw contract for unsupported and corrupt input. Fixtures are built in-process so no binary blobs ship.
/// </summary>
public sealed class DocumentTextExtractorTests
{
    private static DocumentTextExtractor CreateExtractor()
    {
        return new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance);
    }

    [Test]
    public async Task Extractor_WhenPdf_ReturnsText()
    {
        const string expected = "Hello PdfPig Extraction";
        using var stream = new MemoryStream(BuildSingleLinePdf(expected));

        var result = await CreateExtractor().ExtractAsync(stream, "sample.pdf", ".pdf", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, result.Status);
        var markdown = AssertEx.NotNull(result.Markdown);
        AssertEx.Contains(markdown, expected);
        AssertEx.Equal(markdown.Length, result.ExtractedChars ?? -1);
    }

    [Test]
    public async Task Extractor_WhenDocx_ReturnsText()
    {
        using var stream = new MemoryStream(BuildDocx());

        var result = await CreateExtractor().ExtractAsync(stream, "report.docx", ".docx", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, result.Status);
        var markdown = AssertEx.NotNull(result.Markdown);
        // Plain text is the floor; the heading style and bold run are layered on as light Markdown.
        AssertEx.Contains(markdown, "Project Overview");
        AssertEx.Contains(markdown, "the quick brown fox");
        AssertEx.Contains(markdown, "# Project Overview");
        AssertEx.Contains(markdown, "**Important:");
    }

    [Test]
    public async Task Extractor_WhenPlaintextLegacyEncoding_DecodesCorrectly()
    {
        // BOM-less Windows-1252 bytes: 0x97 is an em dash (U+2014) and 0xE9 is 'é' — both distinguish a correct
        // code-page decode from a naive Latin-1 / UTF-8 read.
        var bytes = BuildWindows1252Fixture();
        using var stream = new MemoryStream(bytes);

        var result = await CreateExtractor().ExtractAsync(stream, "legacy.txt", ".txt", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, result.Status);
        var markdown = AssertEx.NotNull(result.Markdown);
        AssertEx.Contains(markdown, "café");
        AssertEx.Contains(markdown, "naïve");
        AssertEx.Contains(markdown, "—", StringComparison.Ordinal, "the em dash (U+2014) proves a Windows-1252 decode, not Latin-1.");
    }

    [Test]
    public async Task Extractor_WhenMarkdown_PassesContentThrough()
    {
        const string content = "# Title\n\nA paragraph with **bold** text.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await CreateExtractor().ExtractAsync(stream, "notes.md", ".md", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, result.Status);
        AssertEx.Equal(content, result.Markdown);
    }

    [Test]
    public async Task Extractor_WhenUnsupportedExtension_ReturnsUnsupported()
    {
        // A PNG signature — a binary type with no pure-.NET text reader.
        using var stream = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var result = await CreateExtractor().ExtractAsync(stream, "image.png", ".png", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Unsupported, result.Status);
        AssertEx.Null(result.Markdown);
        AssertEx.Null(result.Error);
    }

    [Test]
    public async Task Extractor_WhenCorruptDocument_ReturnsFailedWithoutThrowing()
    {
        // Declared as a PDF but the bytes are not a PDF: the reader throws and the extractor must absorb it.
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("this is not a pdf at all"));

        var result = await CreateExtractor().ExtractAsync(stream, "broken.pdf", ".pdf", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Null(result.Markdown);
        AssertEx.NotNull(result.Error);
    }

    [Test]
    public async Task Extractor_WhenStructuredOutputExceedsCap_ReturnsFailedWithoutContent()
    {
        // The structured path returns the document verbatim; bound its aggregate size so a huge document cannot reach
        // chunking/persistence unbounded. A 1000-char body against a 10-char cap must fail cleanly.
        var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance,
            maxOutputChars: 5_000_000,
            maxStructuredOutputChars: 10,
            maxExpansionRatio: 200,
            minCharsForExpansionGuard: 1_000_000);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 1000)));

        var result = await extractor.ExtractStructuredAsync(stream, "big.md", ".md", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Null(result.Document);
        AssertEx.Contains(AssertEx.NotNull(result.Error), "maximum extractable size");
    }

    [Test]
    public async Task Extractor_WhenOutputExpandsBeyondRatio_ReturnsFailedWithoutContent()
    {
        // Expansion-ratio guard: any output above the floor whose char count exceeds inputBytes * ratio is a bomb
        // signature. A ratio of 0 makes every above-floor output trip it, isolating the guard from the absolute cap.
        var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance,
            maxOutputChars: 5_000_000,
            maxStructuredOutputChars: 20_000_000,
            maxExpansionRatio: 0,
            minCharsForExpansionGuard: 10);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 100)));

        var result = await extractor.ExtractAsync(stream, "expands.md", ".md", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Null(result.Markdown);
        AssertEx.Contains(AssertEx.NotNull(result.Error), "size ratio");
    }

    [Test]
    public async Task Extractor_WhenZipDeclaresOversizeUncompressed_RejectsBeforeParsing()
    {
        // A real, well-formed zip whose single entry declares ~3 MiB uncompressed (highly compressible zero bytes, so the
        // stored bytes are tiny). It is a valid zip but NOT a valid .docx, so if the preflight let it through the DOCX
        // reader would fail with a generic "Extraction failed (...)". Asserting the declared-size preflight reason instead
        // proves rejection happened up front, from the central directory, before the reader ever parsed the container.
        var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance,
            maxOutputChars: 5_000_000,
            maxStructuredOutputChars: 20_000_000,
            maxExpansionRatio: 200,
            minCharsForExpansionGuard: 1_000_000,
            maxDeclaredUncompressedBytes: 1024 * 1024,
            maxCompressionRatio: 1_000_000);
        using var stream = new MemoryStream(BuildZip(entryCount: 1, bytesPerEntry: 3 * 1024 * 1024));

        var result = await extractor.ExtractAsync(stream, "bomb.docx", ".docx", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Null(result.Markdown);
        AssertEx.Contains(AssertEx.NotNull(result.Error), "uncompressed");
    }

    [Test]
    public async Task Extractor_WhenZipDeclaresTooManyEntries_RejectsBeforeParsing()
    {
        // 50 tiny entries against a 10-entry ceiling: the entry-count guard fires while summing the central directory,
        // before any entry is decompressed.
        var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance,
            maxOutputChars: 5_000_000,
            maxStructuredOutputChars: 20_000_000,
            maxExpansionRatio: 200,
            minCharsForExpansionGuard: 1_000_000,
            maxCompressedEntries: 10);
        using var stream = new MemoryStream(BuildZip(entryCount: 50, bytesPerEntry: 16));

        var result = await extractor.ExtractAsync(stream, "many.docx", ".docx", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Contains(AssertEx.NotNull(result.Error), "entries");
    }

    [Test]
    public async Task Extractor_WhenZipDeclaresOversizeRatio_RejectsBeforeParsing()
    {
        // A ~256 KiB zero-byte entry compresses to a few hundred bytes — a several-hundred-x declared ratio. A ceiling of
        // 1x makes any real archive trip the ratio guard, isolating it from the entry-count and absolute-size checks.
        var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance,
            maxOutputChars: 5_000_000,
            maxStructuredOutputChars: 20_000_000,
            maxExpansionRatio: 200,
            minCharsForExpansionGuard: 1_000_000,
            maxCompressionRatio: 1);
        using var stream = new MemoryStream(BuildZip(entryCount: 1, bytesPerEntry: 256 * 1024));

        var result = await extractor.ExtractAsync(stream, "ratio.docx", ".docx", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Contains(AssertEx.NotNull(result.Error), "expansion ratio");
    }

    [Test]
    public async Task Extractor_WhenHealthyDocx_PassesPreflight()
    {
        // A genuine small .docx must clear the preflight at the shipped default ceilings and extract normally — the guard
        // rejects only pathological archives, never ordinary documents.
        using var stream = new MemoryStream(BuildDocx());

        var result = await CreateExtractor().ExtractAsync(stream, "report.docx", ".docx", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, result.Status);
        AssertEx.Contains(AssertEx.NotNull(result.Markdown), "Project Overview");
    }

    [Test]
    public async Task Extractor_WhenPdfExceedsPageCap_RejectsBeforeExtractingText()
    {
        // A valid 3-page PDF against a 2-page ceiling. The page count is read from the catalog on open, before per-page
        // text extraction, so the cap fires without materializing the pages' text.
        var extractor = new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance,
            maxOutputChars: 5_000_000,
            maxStructuredOutputChars: 20_000_000,
            maxExpansionRatio: 200,
            minCharsForExpansionGuard: 1_000_000,
            maxPdfPageCount: 2);
        using var stream = new MemoryStream(BuildMultiPagePdf(pageCount: 3));

        var result = await extractor.ExtractAsync(stream, "long.pdf", ".pdf", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Null(result.Markdown);
        AssertEx.Contains(AssertEx.NotNull(result.Error), "pages");
    }

    [Test]
    public async Task Extractor_WhenZipDeclaresZip64OverEntryCount_RejectsWithoutMaterializing()
    {
        // The classic EOCD total-entries field is saturated to 0xFFFF and a Zip64 EOCD record declares millions of
        // entries. The declared count is read straight from the EOCD/Zip64 records with ZERO ZipArchiveEntry
        // allocations, so this rejects up front at the shipped 10,000-entry ceiling. The crafted bytes are NOT an
        // openable ZipArchive, so had the count NOT been read from the EOCD the code would have fallen through to the
        // DOCX reader and failed with a generic "Extraction failed (...)". Asserting the "entries" reason proves the
        // rejection happened at the EOCD count check, before any entry object was materialized.
        using var stream = new MemoryStream(BuildZip64EntryCountBomb(declaredTotalEntries: 5_000_000));

        var result = await CreateExtractor().ExtractAsync(stream, "bomb.docx", ".docx", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Null(result.Markdown);
        AssertEx.Contains(AssertEx.NotNull(result.Error), "entries");
    }

    [Test]
    public void EvaluateDeclaredZipSizes_WhenAggregateOverflows_Rejects()
    {
        // Two individually-valid Zip64 uncompressed sizes that sum past long.MaxValue. Unchecked addition would wrap the
        // running total to a small (or negative) number and slip an oversize archive past the absolute ceiling; the
        // saturating aggregation must clamp at long.MaxValue and REJECT instead.
        (long Length, long CompressedLength)[] entries =
        [
            (long.MaxValue - 10, 100),
            (long.MaxValue - 10, 100)
        ];

        var reason = DocumentTextExtractor.EvaluateDeclaredZipSizes(entries,
            maxDeclaredUncompressedBytes: 512L * 1024 * 1024,
            maxCompressionRatio: 200);

        AssertEx.Contains(AssertEx.NotNull(reason), "uncompressed");
    }

    [Test]
    public void EvaluateDeclaredZipSizes_WhenEntrySizeNegative_Rejects()
    {
        // A high-bit-set Zip64 size reads back as a negative long. A negative term would drag the running total down and
        // mask an oversize archive, so hostile negative sizes must reject outright.
        (long Length, long CompressedLength)[] entries = [(-1, 100)];

        var reason = DocumentTextExtractor.EvaluateDeclaredZipSizes(entries,
            maxDeclaredUncompressedBytes: 512L * 1024 * 1024,
            maxCompressionRatio: 200);

        AssertEx.Contains(AssertEx.NotNull(reason), "invalid entry size");
    }

    [Test]
    public async Task Extractor_WhenMalformedPdf_RejectsAtPreflightWithoutReparsing()
    {
        // Bytes declared as a PDF but not a PDF: the preflight's own PdfDocument.Open throws a PdfPig format exception,
        // so the document is failed up front with the content-free "could not be parsed" reason and is NOT handed to the
        // reader for a wasted second parse. Asserting that reason — not the reader's generic "Extraction failed (...)" —
        // proves the no-reparse preflight path handled it.
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("this is not a pdf at all"));

        var result = await CreateExtractor().ExtractAsync(stream, "broken.pdf", ".pdf", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Failed, result.Status);
        AssertEx.Null(result.Markdown);
        AssertEx.Contains(AssertEx.NotNull(result.Error), "could not be parsed");
    }

    [Test]
    public async Task Extractor_WhenStructuredWithinBounds_ExtractsDocument()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Title\n\nA short paragraph."));

        var result = await CreateExtractor().ExtractStructuredAsync(stream, "notes.md", ".md", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, result.Status);
        AssertEx.NotNull(result.Document);
        AssertEx.Null(result.Error);
    }

    [Test]
    public void IsSupported_NormalizesCaseAndLeadingDot()
    {
        var extractor = CreateExtractor();

        AssertEx.True(extractor.IsSupported(".pdf"), "lowercase .pdf is supported.");
        AssertEx.True(extractor.IsSupported(".DOCX"), "uppercase .DOCX resolves case-insensitively.");
        AssertEx.True(extractor.IsSupported("txt"), "a missing leading dot is tolerated.");
        AssertEx.False(extractor.IsSupported(".png"), "images are not supported in v1.");
        AssertEx.False(extractor.IsSupported(string.Empty), "an empty extension is not supported.");
    }

    private static byte[] BuildWindows1252Fixture()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var windows1252 = Encoding.GetEncoding(1252);
        var sentence = "Déjà vu — naïve café señor. The “smart” quotes and em dash distinguish Windows-1252 from Latin-1. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 6));
        return windows1252.GetBytes(text);
    }

    private static byte[] BuildDocx()
    {
        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            // Append children one at a time: the constructor-with-children overloads trip Sonar S3220.
            var heading = body.AppendChild(new Paragraph());
            heading.AppendChild(new ParagraphProperties()).AppendChild(new ParagraphStyleId
            {
                Val = "Heading1"
            });
            heading.AppendChild(new Run()).AppendChild(new Text("Project Overview"));

            var paragraph = body.AppendChild(new Paragraph());
            var boldRun = paragraph.AppendChild(new Run());
            boldRun.AppendChild(new RunProperties()).AppendChild(new Bold());
            boldRun.AppendChild(new Text("Important: ")
            {
                Space = SpaceProcessingModeValues.Preserve
            });
            paragraph.AppendChild(new Run()).AppendChild(new Text("the quick brown fox."));

            mainPart.Document.Save();
        }

        return buffer.ToArray();
    }

    // A real, well-formed zip built in-process. Each entry is `bytesPerEntry` zero bytes — highly compressible, so the
    // stored size is tiny while the central directory declares the full uncompressed length. This is the honest shape a
    // preflight rejects on declared metadata (entry count / declared size / declared ratio) without decompressing.
    private static byte[] BuildZip(int entryCount, int bytesPerEntry)
    {
        var payload = new byte[bytesPerEntry];
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < entryCount; i++)
            {
                var entry = archive.CreateEntry(string.Create(CultureInfo.InvariantCulture, $"part{i}.xml"),
                    CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(payload, 0, payload.Length);
            }
        }

        return buffer.ToArray();
    }

    // A hand-built binary trailer that models an entry-count zip bomb whose true count lives in the Zip64 records:
    // a Zip64 EOCD record (declaring `declaredTotalEntries`), the Zip64 EOCD locator pointing at it, then a classic
    // EOCD whose total-entries field is saturated to 0xFFFF. Only the signatures and the entry-count fields the
    // preflight reads are populated; the bytes are deliberately NOT an openable ZipArchive, so a passing test proves the
    // count was read from the EOCD records rather than by materializing entries.
    private static byte[] BuildZip64EntryCountBomb(long declaredTotalEntries)
    {
        const int Zip64EocdSize = 56;
        const int Zip64LocatorSize = 20;
        const int EocdSize = 22;

        var bytes = new byte[Zip64EocdSize + Zip64LocatorSize + EocdSize];
        var span = bytes.AsSpan();

        // Zip64 EOCD record at offset 0.
        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(4, 8), Zip64EocdSize - 12); // size of the record remainder
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(24, 8), declaredTotalEntries); // entries on this disk
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(32, 8), declaredTotalEntries); // total entries

        // Zip64 EOCD locator at offset 56, pointing back at the record at offset 0.
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(Zip64EocdSize, 4), 0x07064b50);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(Zip64EocdSize + 8, 8), 0); // relative offset of the record
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(Zip64EocdSize + 16, 4), 1); // total number of disks

        // Classic EOCD at offset 76 with the total-entries field saturated to 0xFFFF (defers to the Zip64 record).
        var eocd = Zip64EocdSize + Zip64LocatorSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(eocd, 4), 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(eocd + 8, 2), 0xFFFF); // entries on this disk
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(eocd + 10, 2), 0xFFFF); // total entries
        // Comment length (offset eocd + 20) stays 0, so the record ends exactly at the stream end.

        return bytes;
    }

    // A minimal hand-authored multi-page PDF with correct xref byte offsets: catalog (obj 1), the pages tree (obj 2),
    // then a Page + Contents object pair per page, and a shared font as the final object. Mirrors the single-page
    // fixture's structure so PdfPig parses it and reports NumberOfPages == pageCount.
    private static byte[] BuildMultiPagePdf(int pageCount)
    {
        var fontObject = (2 * pageCount) + 3;

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>"
        };

        var kids = new StringBuilder();
        for (var i = 0; i < pageCount; i++)
        {
            kids.Append(3 + (2 * i)).Append(" 0 R ");
        }

        bodies.Add(string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /Pages /Kids [{kids.ToString().TrimEnd()}] /Count {pageCount} >>"));

        for (var i = 0; i < pageCount; i++)
        {
            var contentsObject = 4 + (2 * i);
            bodies.Add(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {contentsObject} 0 R >>"));

            var content = string.Create(CultureInfo.InvariantCulture, $"BT /F1 24 Tf 72 700 Td (Page {i + 1}) Tj ET");
            bodies.Add("<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream");
        }

        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");

        var offsets = new int[bodies.Count + 1];
        for (var i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        var startxref = Encoding.ASCII.GetByteCount(builder.ToString());
        var size = bodies.Count + 1;
        builder.Append("xref\n0 ").Append(size).Append('\n');
        builder.Append("0000000000 65535 f \n");
        for (var i = 1; i < size; i++)
        {
            builder.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(startxref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    // PdfPig is read-only, so the fixture is a minimal hand-authored single-page PDF with correct xref byte offsets
    // and a single text-showing operator. ~600 bytes, fully ASCII.
    private static byte[] BuildSingleLinePdf(string text)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var content = "BT /F1 24 Tf 72 700 Td (" + text + ") Tj ET";
        var contentObject = "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");

        var offsets = new int[6];

        int CurrentOffset() =>
            Encoding.ASCII.GetByteCount(builder.ToString());

        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = CurrentOffset();
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        offsets[5] = CurrentOffset();
        builder.Append("5 0 obj\n").Append(contentObject).Append("\nendobj\n");

        var startxref = CurrentOffset();
        builder.Append("xref\n0 6\n");
        builder.Append("0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
        {
            builder.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(startxref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
