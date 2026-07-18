namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Text;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Options;

/// <summary>
///     Offline, deterministic header-boundary chunker (no tokenizer or external package). It walks the document's ordered
///     element stream: each <see cref="IngestionDocumentHeader" /> opens a new section (maintaining an "H1 &gt; H2"
///     heading trail via a level stack); paragraphs and tables accumulate into their section's body, which is then split
///     into overlapping chunks. Content appearing before the first header falls into a single implicit section. The same
///     document always yields the same sections and chunks.
///     <para>
///         Chunk sizing is TOKEN-AWARE (RAG-08): a section body is cut at whichever bound — a per-chunk token budget
///         (<see cref="KnowledgeBaseOptions.MaxChunkTokens" />, optionally tightened to the resolved embedding model's
///         context window) or the hard character ceiling (<see cref="KnowledgeBaseOptions.MaxChunkChars" />) — is reached
///         first, always breaking at a whitespace boundary. The per-window token budget is reduced by the section's
///         heading-trail cost so the embedded heading-prefixed <see cref="KnowledgeChunk.ContextualContent" /> stays within
///         the window. Token counts are a deterministic, dependency-free approximation (see
///         <see cref="ChunkTokenApproximation" />): weighted characters ÷ 4, with CJK/emoji weighted heavier so a
///         token-dense script does not silently produce chunks several times the intended token size. The token budget can
///         only TIGHTEN the effective size, never enlarge it past the character ceiling, so plain ASCII prose keeps the
///         character ceiling as its binding bound (identical to the pre-RAG-08 behavior — no reindex needed for existing
///         ASCII corpora), while CJK/token-dense content and smaller embedder windows yield correspondingly smaller chunks.
///     </para>
/// </summary>
public sealed class HeaderBoundaryChunkingService : IChunkingService
{
    private const int MinHeadingLevel = 1;
    private const int MaxHeadingLevel = 6;

    // Tokens reserved off a resolved embedding window before it becomes the chunk budget: covers the "search_document: "
    // intent prefix the embedder prepends plus the model's own special tokens (CLS/SEP/etc.), so the embedded text never
    // reaches the raw window limit.
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
            sections.Add(new KnowledgeChunkingSection(ordinal, build.Heading, build.Level));

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
                    ? window
                    : string.Concat(build.HeadingPath, "\n\n", window);

                chunks.Add(new KnowledgeChunk(chunkIndex,
                    ordinal,
                    window,
                    contextual,
                    build.HeadingPath,
                    ChunkTokenApproximation.EstimateTokens(window)));
                chunkIndex++;
            }
        }

        return new KnowledgeChunkingResult(sections, chunks);
    }

    // The per-chunk token budget: the configured MaxChunkTokens, tightened to the resolved embedding window (minus a
    // safety reserve) when one is known. The window can only lower the budget, never raise it, so a large-window model
    // never enlarges chunks past the configured granularity and existing corpora chunk identically.
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

    // Walks the flat ordered element stream. Headers open sections and drive the heading trail; other elements append
    // their rendered text to the current section's body. Pre-heading content lands in a single implicit section that is
    // ordered first when it holds any content.
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

                current = new SectionBuild(heading, level, headingPath);
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
                implicitSection ??= new SectionBuild(Heading: null, Level: null, HeadingPath: null);
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

    // Splits a section body into windows bounded by BOTH a character ceiling (maxChars) and an estimated token budget
    // (windowTokenBudget), whichever is reached first, breaking at the last whitespace before the limit so a word is not
    // cut, and carrying `overlap` trailing characters into the next window. Deterministic; start strictly advances.
    private static IEnumerable<string> SplitIntoWindows(string text, int maxChars, int overlap, int windowTokenBudget)
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
                yield return window;
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

    // The hard end index for a window starting at `start`: the smaller of the character ceiling and the position at which
    // the estimated token budget is reached. The scan is bounded by the character limit, so the character ceiling always
    // remains a hard upper bound and the token budget can only shrink the window, never grow it. At least one character is
    // always consumed (a single heavy character that alone exceeds the budget still advances) so the walk cannot stall.
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

    private sealed record SectionBuild(string? Heading, int? Level, string? HeadingPath)
    {
        public StringBuilder Body { get; } = new();
    }
}
