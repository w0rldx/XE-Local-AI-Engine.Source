namespace XE_Local_AI_Engine.Tests.DocumentIngestion;

using System.Text;
using Microsoft.Extensions.DataIngestion;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class PlaintextDocumentReaderTests
{
    [Test]
    public async Task Markdown_HeadingsBecomeHeaders_AndRenderedTextRoundTrips()
    {
        const string content = "# Title\n\nIntro.\n\n```text\n# literal\n\nline\n```\n\n- one\n- two\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n## Next\n\nTail";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await CreateExtractor().ExtractStructuredAsync(stream, "notes.md", ".md", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, result.Status);
        var elements = AssertEx.NotNull(result.Document).EnumerateContent().ToList();
        var headers = elements.OfType<IngestionDocumentHeader>().ToList();
        AssertEx.Equal(2, headers.Count);
        AssertEx.Equal("# Title", headers[0].Text);
        AssertEx.Equal(1, headers[0].Level ?? -1);
        AssertEx.Equal("## Next", headers[1].Text);
        AssertEx.Equal(2, headers[1].Level ?? -1);
        AssertEx.True(elements.Where(static element => element.Text?.Contains("# literal", StringComparison.Ordinal) == true)
                              .All(static element => element is IngestionDocumentParagraph),
            "A heading-shaped line inside a fenced block must remain body text.");

        using var roundTripStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var roundTrip = await CreateExtractor().ExtractAsync(roundTripStream, "notes.md", ".md", CancellationToken.None);
        AssertEx.Equal(content, AssertEx.NotNull(roundTrip.Markdown));
    }

    [Test]
    public async Task Html_EmitsVisibleHeadingsAndText_WithoutScriptOrStyleContent()
    {
        const string html = "<h1>Title &amp; More</h1><p>Hello <strong>world</strong>.</p>"
                            + "<script>window.evil = 'secret';</script><style>.hidden { color: red; }</style>"
                            + "<h2>Details</h2><pre>line 1\nline 2</pre>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));

        var result = await CreateExtractor().ExtractStructuredAsync(stream, "page.html", ".html", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, result.Status);
        var elements = AssertEx.NotNull(result.Document).EnumerateContent().ToList();
        var headers = elements.OfType<IngestionDocumentHeader>().ToList();
        AssertEx.Equal(2, headers.Count);
        AssertEx.Equal("# Title & More", headers[0].Text);
        AssertEx.Equal("## Details", headers[1].Text);

        var visible = string.Join("\n", elements.Select(static element => element.Text));
        AssertEx.Contains(visible, "Hello world.");
        AssertEx.Contains(visible, "line 1\nline 2");
        AssertEx.False(visible.Contains("window.evil", StringComparison.Ordinal), "Script content must not be retained.");
        AssertEx.False(visible.Contains(".hidden", StringComparison.Ordinal), "Style content must not be retained.");
    }

    [Test]
    public async Task StructuredPlaintext_SplitsOnlyAtLosslessLogicalBoundaries()
    {
        const string content = "2026-08-13 start\ncontinuation\n\n2026-08-13 ready\n\n{\"status\":\"ok\"}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var structured = await CreateExtractor().ExtractStructuredAsync(stream, "service.log", ".log", CancellationToken.None);

        AssertEx.Equal(DocumentExtractionStatus.Extracted, structured.Status);
        var elements = AssertEx.NotNull(structured.Document).EnumerateContent().ToList();
        AssertEx.Equal(3, elements.Count);
        AssertEx.True(elements.All(static element => element is IngestionDocumentParagraph));

        using var roundTripStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var roundTrip = await CreateExtractor().ExtractAsync(roundTripStream, "service.log", ".log", CancellationToken.None);
        AssertEx.Equal(content, AssertEx.NotNull(roundTrip.Markdown));
    }

    private static DocumentTextExtractor CreateExtractor()
    {
        return new DocumentTextExtractor(Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentTextExtractor>.Instance);
    }
}
