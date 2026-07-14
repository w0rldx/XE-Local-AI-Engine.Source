namespace XE_Local_AI_Engine.Tests.DocumentIngestion;

using System.Globalization;
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
