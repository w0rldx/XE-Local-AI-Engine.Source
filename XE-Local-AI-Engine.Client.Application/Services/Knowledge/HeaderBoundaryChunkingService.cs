namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Options;

/// <summary>
///     Offline, deterministic header-boundary chunker: it walks the document's ordered element stream, opening a new
///     section on each <see cref="IngestionDocumentHeader" /> and splitting each section body into overlapping chunks.
/// </summary>
/// <remarks>
///     No tokenizer or external package is involved. A section maintains an "H1 &gt; H2" heading trail via a level
///     stack; paragraphs and tables accumulate into their section body, and content before the first header falls into a
///     single implicit section. Chunk sizing is TOKEN-AWARE and can only tighten the character ceiling, never enlarge
///     it. The same document always yields the same sections and chunks. Sizing rules and their rationale:
///     <c>docs/wiki/15-knowledge-base.md</c> ("Ingestion pipeline").
/// </remarks>
public sealed class HeaderBoundaryChunkingService : IChunkingService
{
    private const int MinHeadingLevel = 1;
    private const int MaxHeadingLevel = 6;

    // Tokens reserved off a resolved embedding window before it becomes the chunk budget: the "search_document: " intent
    // prefix the embedder prepends plus the model's own special tokens, so embedded text never reaches the raw window.
    private const int EmbeddingWindowReserveTokens = 32;

    // Floor for the per-chunk token budget when a resolved window is very small, so a tiny/misconfigured window cannot
    // collapse chunking to near-single-character windows.
    private const int MinChunkTokenBudget = 32;

    // Floor for the per-WINDOW token budget after the section's heading-trail cost is subtracted, so a very long heading
    // trail cannot collapse a section's content windows to nothing.
    private const int MinWindowTokenBudget = 16;

    private readonly KnowledgeBaseOptions _options;

    public HeaderBoundaryChunkingService(IOptions<KnowledgeBaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public KnowledgeChunkingResult Chunk(IngestionDocument document, int? embeddingContextWindowTokens = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var maxChars = Math.Max(1, _options.MaxChunkChars);
        // Overlap can never reach the window size or the sliding window would not advance.
        var overlap = Math.Clamp(_options.ChunkOverlapChars, 0, maxChars - 1);
        var chunkTokenBudget = ResolveChunkTokenBudget(embeddingContextWindowTokens);

        var builds = BuildSections(document);
        var sections = new List<KnowledgeChunkingSection>(builds.Count);
        var chunks = new List<KnowledgeChunk>();
        var chunkIndex = 0;

        for (var ordinal = 0; ordinal < builds.Count; ordinal++)
        {
            var build = builds[ordinal];
            sections.Add(new KnowledgeChunkingSection
            {
                Ordinal = ordinal,
                Heading = build.Heading,
                Level = build.Level,
                PageNumber = build.PageNumber
            });

            var body = build.Body.ToString().Trim();
            if (body.Length == 0)
            {
                continue;
            }

            // Reserve the heading-trail tokens from the window budget so the embedded contextual text (heading + body
            // window) fits the same budget; the separator matches the one used to build ContextualContent below.
            var headingCostTokens = build.HeadingPath is null
                ? 0
                : ChunkTokenApproximation.EstimateTokens(string.Concat(build.HeadingPath, "\n\n"));
            var windowTokenBudget = Math.Max(MinWindowTokenBudget, chunkTokenBudget - headingCostTokens);

            foreach (var window in SplitIntoWindows(body, maxChars, overlap, windowTokenBudget))
            {
                var contextual = build.HeadingPath is null
                    ? window.Content
                    : string.Concat(build.HeadingPath, "\n\n", window.Content);

                chunks.Add(new KnowledgeChunk
                {
                    ChunkIndex = chunkIndex,
                    SectionOrdinal = ordinal,
                    Content = window.Content,
                    ContextualContent = contextual,
                    HeadingPath = build.HeadingPath,
                    TokenCount = ChunkTokenApproximation.EstimateTokens(window.Content),
                    PageNumber = build.PageNumber,
                    StartOffset = window.StartOffset,
                    EndOffset = window.EndOffset,
                    ContentHash = Hash(window.Content),
                    EmbeddingInputHash = Hash(contextual)
                });
                chunkIndex++;
            }
        }

        return new KnowledgeChunkingResult
        {
            Sections = sections,
            Chunks = chunks
        };
    }

    // The per-chunk token budget: configured MaxChunkTokens, tightened to a known resolved embedding window minus the
    // safety reserve. A window only lowers it, so a big-window model never enlarges chunks and corpora chunk identically.
    private int ResolveChunkTokenBudget(int? embeddingContextWindowTokens)
    {
        var configuredBudget = Math.Max(1, _options.MaxChunkTokens);
        if (embeddingContextWindowTokens is not int window || window <= 0)
        {
            return configuredBudget;
        }

        var windowBudget = Math.Max(MinChunkTokenBudget, window - EmbeddingWindowReserveTokens);
        return Math.Min(configuredBudget, windowBudget);
    }

    // Walks the flat ordered element stream: headers open sections and drive the heading trail, other elements append
    // rendered text to the current section body; pre-heading content forms an implicit section, ordered first if non-empty.
    private static List<SectionBuild> BuildSections(IngestionDocument document)
    {
        var headerSections = new List<SectionBuild>();
        var headingStack = new List<HeadingFrame>();
        SectionBuild? implicitSection = null;
        SectionBuild? current = null;

        foreach (var element in document.EnumerateContent())
        {
            if (element is IngestionDocumentHeader header)
            {
                var level = Math.Clamp(header.Level ?? MinHeadingLevel, MinHeadingLevel, MaxHeadingLevel);
                var heading = CleanHeading(header.Text);

                // A same-or-shallower heading closes deeper trail entries, then this heading joins the trail.
                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                {
                    headingStack.RemoveAt(headingStack.Count - 1);
                }

                headingStack.Add(new HeadingFrame(level, heading));
                var headingPath = string.Join(" > ", headingStack.Select(frame => frame.Text));

                current = new SectionBuild
                {
                    Heading = heading,
                    Level = level,
                    HeadingPath = headingPath,
                    PageNumber = header.PageNumber
                };
                headerSections.Add(current);
                continue;
            }

            var text = element.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (current is null)
            {
                implicitSection ??= new SectionBuild
                {
                    Heading = null,
                    Level = null,
                    HeadingPath = null,
                    PageNumber = element.PageNumber
                };
                current = implicitSection;
            }

            _ = current.Body.Append(text).Append("\n\n");
        }

        var ordered = new List<SectionBuild>(headerSections.Count + 1);
        if (implicitSection is not null && implicitSection.Body.Length > 0)
        {
            ordered.Add(implicitSection);
        }

        ordered.AddRange(headerSections);
        return ordered;
    }

    // Splits a section body into windows bounded by BOTH maxChars and windowTokenBudget, whichever is hit first, breaking
    // at the last whitespace before the limit so no word is cut and carrying `overlap` chars over; start strictly advances.
    private static IEnumerable<ChunkWindow> SplitIntoWindows(string text, int maxChars, int overlap, int windowTokenBudget)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = ResolveWindowEnd(text, start, maxChars, windowTokenBudget);
            if (end < text.Length)
            {
                var breakPos = LastWhitespaceBefore(text, start, end);
                if (breakPos > start)
                {
                    end = breakPos;
                }
            }

            var window = text[start..end].Trim();
            if (window.Length > 0)
            {
                var leadingWhitespace = FindLeadingContent(text.AsSpan(start, end - start));
                var actualStart = leadingWhitespace < 0 ? start : start + leadingWhitespace;
                yield return new ChunkWindow(window, actualStart, actualStart + window.Length);
            }

            if (end >= text.Length)
            {
                yield break;
            }

            var nextStart = end - overlap;
            if (nextStart <= start)
            {
                nextStart = end;
            }

            start = nextStart;
        }
    }

    private static int FindLeadingContent(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    // The hard end index for a window: the smaller of the character ceiling and where the token budget is reached, so the
    // ceiling stays a hard upper bound; at least one character is always consumed, so a heavy character cannot stall it.
    private static int ResolveWindowEnd(string text, int start, int maxChars, int windowTokenBudget)
    {
        var charLimit = Math.Min(start + maxChars, text.Length);
        if (windowTokenBudget <= 0)
        {
            return charLimit;
        }

        var weightedBudget = (long)windowTokenBudget * ChunkTokenApproximation.CharsPerToken;
        long weighted = 0;
        for (var index = start; index < charLimit; index++)
        {
            weighted += ChunkTokenApproximation.WeightOf(text[index]);
            if (weighted >= weightedBudget)
            {
                return index + 1;
            }
        }

        return charLimit;
    }

    private static int LastWhitespaceBefore(string text, int start, int end)
    {
        for (var index = end - 1; index > start; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static string CleanHeading(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text.TrimStart('#', ' ', '\t').Trim();
    }

    private readonly record struct HeadingFrame(int Level, string Text);

    private readonly record struct ChunkWindow(string Content, int StartOffset, int EndOffset);

    private static string Hash(string content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private sealed record SectionBuild
    {
        public required string? Heading { get; init; }

        public required int? Level { get; init; }

        public required string? HeadingPath { get; init; }

        public required int? PageNumber { get; init; }

        public StringBuilder Body { get; } = new();
    }
}
