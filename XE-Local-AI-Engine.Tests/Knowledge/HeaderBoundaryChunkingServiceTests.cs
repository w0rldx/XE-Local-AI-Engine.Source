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

    [Test]
    public void Chunk_WhenTokenBudgetIsTighterThanCharCeiling_SplitsTokenDenseContentThatCharSplittingWouldNot()
    {
        // Both bodies are 400 characters and BOTH fit the 1000-char ceiling as a single window, so pure character
        // splitting would yield one chunk for each. Token-aware sizing splits the CJK body because each ideograph costs
        // ~4x the tokens of an ASCII character, exceeding the 150-token budget, while the ASCII body stays a single chunk.
        var service = CreateService(maxChars: 1000, overlap: 0, maxTokens: 150);
        var asciiBody = new string('a', count: 400);
        var cjkBody = new string(CjkIdeograph, count: 400);

        var ascii = service.Chunk(BuildDocument(Paragraph(asciiBody)));
        var cjk = service.Chunk(BuildDocument(Paragraph(cjkBody)));

        AssertEx.Equal(expected: 1, ascii.Chunks.Count);
        AssertEx.True(cjk.Chunks.Count > 1,
            "Token-dense (CJK) content must split on the token budget even though its character length fits the char ceiling.");
        AssertEx.True(cjk.Chunks.All(chunk => chunk.TokenCount <= 150),
            "Every token-aware chunk must respect the per-chunk token budget.");
    }

    [Test]
    public void Chunk_WhenEmbeddingContextWindowSupplied_TightensChunkSizeBelowTheConfiguredBudget()
    {
        // The configured budget (512) admits the whole 400-char ASCII body as one chunk; supplying a small resolved
        // embedding window forces the per-chunk token budget down (window minus the safety reserve), splitting the body.
        var service = CreateService(maxChars: 1000, overlap: 0, maxTokens: 512);
        var document = BuildDocument(Paragraph(new string('a', count: 400)));

        var withoutWindow = service.Chunk(document, embeddingContextWindowTokens: null);
        var withSmallWindow = service.Chunk(document, embeddingContextWindowTokens: 64);

        AssertEx.Equal(expected: 1, withoutWindow.Chunks.Count);
        AssertEx.True(withSmallWindow.Chunks.Count > withoutWindow.Chunks.Count,
            "A smaller resolved embedding window must tighten the chunk token budget and produce more, smaller chunks.");
    }

    [Test]
    public void Chunk_WhenAContextWindowIsLargerThanTheConfiguredBudget_DoesNotEnlargeChunksPastTheBudget()
    {
        // A large window must never LOOSEN the configured budget — chunking is identical to the no-window case, so existing
        // corpora are unaffected by discovering a large-window embedder.
        var service = CreateService(maxChars: 100000, overlap: 20, maxTokens: 40);
        var document = BuildDocument(Paragraph(string.Join(" ", Enumerable.Repeat("word", count: 300))));

        var noWindow = Fingerprint(service.Chunk(document, embeddingContextWindowTokens: null));
        var largeWindow = Fingerprint(service.Chunk(document, embeddingContextWindowTokens: 8192));

        AssertEx.Equal(noWindow, largeWindow);
    }

    [Test]
    public void Chunk_WhenTokenBudgetSplitsASection_PreservesTheHeadingTrailOnEveryChunk()
    {
        // A long body under a nested heading splits on the token budget; the "H1 > H2" trail must ride every resulting
        // chunk so a mid-section chunk stays retrievable in context.
        var service = CreateService(maxChars: 100000, overlap: 0, maxTokens: 20);
        var document = BuildDocument(Header("Alpha", level: 1),
            Header("Beta", level: 2),
            Paragraph(string.Join(" ", Enumerable.Repeat("word", count: 120))));

        var result = service.Chunk(document);

        AssertEx.True(result.Chunks.Count > 1, "The long body should split into multiple token-bounded chunks.");
        AssertEx.True(result.Chunks.All(chunk => chunk.HeadingPath == "Alpha > Beta"),
            "Every chunk of the split section must carry the full heading trail.");
    }

    [Test]
    public void Chunk_WithATokenBudgetAndContextWindow_IsDeterministic()
    {
        var service = CreateService(maxChars: 100000, overlap: 12, maxTokens: 24);
        var document = BuildDocument(Header("Alpha", level: 1),
            Paragraph(string.Join(" ", Enumerable.Repeat("word", count: 200))),
            Header("Beta", level: 2),
            Paragraph(new string(CjkIdeograph, count: 300)));

        var first = Fingerprint(service.Chunk(document, embeddingContextWindowTokens: 256));
        var second = Fingerprint(service.Chunk(document, embeddingContextWindowTokens: 256));

        AssertEx.Equal(first, second);
    }

    private static string Fingerprint(KnowledgeChunkingResult result)
    {
        return string.Join("", result.Chunks.Select(chunk =>
            $"{chunk.ChunkIndex}|{chunk.SectionOrdinal}|{chunk.HeadingPath}|{chunk.Content}|{chunk.ContextualContent}"));
    }

    // A CJK ideograph (U+4E00) — token-dense content whose per-character token cost is ~4x an ASCII character's. Written
    // as an explicit code point (never a literal glyph) so it cannot be confused with a visually identical clone.
    private const char CjkIdeograph = (char)0x4E00;

    private static HeaderBoundaryChunkingService CreateService(int maxChars = 2000, int overlap = 200, int maxTokens = 512)
    {
        return new HeaderBoundaryChunkingService(Options.Create(new KnowledgeBaseOptions
        {
            MaxChunkChars = maxChars,
            ChunkOverlapChars = overlap,
            MaxChunkTokens = maxTokens
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
