namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The offline header-boundary chunker walks a document's ordered element stream: headers open sections and build the
///     "H1 &gt; H2" heading trail, bodies split into character-bounded overlapping windows, and pre-heading content lands
///     in a single implicit section. These tests pin that structural contract plus its determinism. All fixtures are built
///     in-process from <see cref="IngestionDocument" /> elements so no binary blobs ship.
/// </summary>
public sealed class HeaderBoundaryChunkingServiceTests
{
    [Test]
    public void Chunk_WhenHeadersNest_BuildsHeadingPathTrailAndPopsShallowerHeadings()
    {
        var document = BuildDocument(Header("Alpha", level: 1),
            Paragraph("alpha body"),
            Header("Beta", level: 2),
            Paragraph("beta body"),
            Header("Gamma", level: 1),
            Paragraph("gamma body"));

        var result = CreateService().Chunk(document);

        var trail = string.Join("|", result.Chunks.Select(chunk => chunk.HeadingPath));
        AssertEx.Equal("Alpha|Alpha > Beta|Gamma", trail);
    }

    [Test]
    public void Chunk_WhenBodyExceedsMaxChars_SplitsIntoWindowsBoundedByMaxChars()
    {
        var body = string.Join(" ", Enumerable.Repeat("token", count: 60));
        var document = BuildDocument(Paragraph(body));

        var result = CreateService(maxChars: 50, overlap: 10).Chunk(document);

        AssertEx.True(result.Chunks.Count > 1 && result.Chunks.All(chunk => chunk.Content.Length <= 50),
            "A body longer than MaxChunkChars should split into multiple windows, each at most MaxChunkChars.");
    }

    [Test]
    public void Chunk_WhenWindowsSlide_CarriesOverlapCharsIntoTheNextWindow()
    {
        // A run with no whitespace forces the sliding window to advance by the window size minus the overlap, so the
        // second window must start with the trailing overlap characters of the first.
        var document = BuildDocument(Paragraph("0123456789ABCDEFGHIJKLMNOPQRST"));

        var result = CreateService(maxChars: 10, overlap: 3).Chunk(document);

        var firstTail = result.Chunks[0].Content[^3..];
        AssertEx.True(result.Chunks[1].Content.StartsWith(firstTail, StringComparison.Ordinal),
            "The next window should begin with the trailing overlap characters of the previous window.");
    }

    [Test]
    public void Chunk_WhenChunkIsUnderAHeading_ContextualContentIsHeadingTrailThenContent()
    {
        var document = BuildDocument(Header("Alpha", level: 1), Paragraph("the body"));

        var result = CreateService().Chunk(document);

        AssertEx.Equal("Alpha\n\nthe body", result.Chunks[0].ContextualContent);
    }

    [Test]
    public void Chunk_WhenNoHeaders_ProducesOneImplicitSectionWithNoHeadingTrail()
    {
        var document = BuildDocument(Paragraph("only body"));

        var result = CreateService().Chunk(document);

        AssertEx.Equal(expected: 1, result.Sections.Count);
        AssertEx.Null(result.Sections[0].Heading);
        AssertEx.Null(result.Chunks[0].HeadingPath);
        AssertEx.Equal("only body", result.Chunks[0].ContextualContent);
    }

    [Test]
    public void Chunk_WhenGivenTheSameDocumentTwice_ProducesIdenticalOutput()
    {
        var service = CreateService(maxChars: 40, overlap: 8);
        var document = BuildDocument(Header("Alpha", level: 1),
            Paragraph(string.Join(" ", Enumerable.Repeat("word", count: 40))),
            Header("Beta", level: 2),
            Paragraph("beta body"));

        var first = Fingerprint(service.Chunk(document));
        var second = Fingerprint(service.Chunk(document));

        AssertEx.Equal(first, second);
    }

    private static string Fingerprint(KnowledgeChunkingResult result)
    {
        return string.Join("", result.Chunks.Select(chunk =>
            $"{chunk.ChunkIndex}|{chunk.SectionOrdinal}|{chunk.HeadingPath}|{chunk.Content}|{chunk.ContextualContent}"));
    }

    private static HeaderBoundaryChunkingService CreateService(int maxChars = 2000, int overlap = 200)
    {
        return new HeaderBoundaryChunkingService(Options.Create(new KnowledgeBaseOptions
        {
            MaxChunkChars = maxChars,
            ChunkOverlapChars = overlap
        }));
    }

    private static IngestionDocument BuildDocument(params IngestionDocumentElement[] elements)
    {
        var document = new IngestionDocument("test-document");
        var section = new IngestionDocumentSection();
        foreach (var element in elements)
        {
            section.Elements.Add(element);
        }

        document.Sections.Add(section);
        return document;
    }

    private static IngestionDocumentHeader Header(string text, int level)
    {
        return new IngestionDocumentHeader(text)
        {
            Text = text,
            Level = level
        };
    }

    private static IngestionDocumentParagraph Paragraph(string text)
    {
        return new IngestionDocumentParagraph(text)
        {
            Text = text
        };
    }
}
