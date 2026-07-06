namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Text;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Options;

/// <summary>
///     Offline, deterministic header-boundary chunker (no tokenizer or external package). It walks the document's ordered
///     element stream: each <see cref="IngestionDocumentHeader" /> opens a new section (maintaining an "H1 &gt; H2"
///     heading trail via a level stack); paragraphs and tables accumulate into their section's body, which is then split
///     into character-bounded, overlapping chunks. Content appearing before the first header falls into a single implicit
///     section. Approximate token counts are word counts. The same document always yields the same sections and chunks.
/// </summary>
public sealed class HeaderBoundaryChunkingService : IChunkingService
{
    private const int MinHeadingLevel = 1;
    private const int MaxHeadingLevel = 6;

    private readonly KnowledgeBaseOptions _options;

    public HeaderBoundaryChunkingService(IOptions<KnowledgeBaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public KnowledgeChunkingResult Chunk(IngestionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var maxChars = Math.Max(1, _options.MaxChunkChars);
        // Overlap can never reach the window size or the sliding window would not advance.
        var overlap = Math.Clamp(_options.ChunkOverlapChars, 0, maxChars - 1);

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

            foreach (var window in SplitIntoWindows(body, maxChars, overlap))
            {
                var contextual = build.HeadingPath is null
                    ? window
                    : string.Concat(build.HeadingPath, "\n\n", window);

                chunks.Add(new KnowledgeChunk(chunkIndex,
                    ordinal,
                    window,
                    contextual,
                    build.HeadingPath,
                    CountWords(window)));
                chunkIndex++;
            }
        }

        return new KnowledgeChunkingResult(sections, chunks);
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

    // Splits a section body into windows of at most maxChars, breaking at the last whitespace before the limit so a word
    // is not cut, and carrying `overlap` trailing characters into the next window. Deterministic; start strictly advances.
    private static IEnumerable<string> SplitIntoWindows(string text, int maxChars, int overlap)
    {
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + maxChars, text.Length);
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

    private static int CountWords(string content)
    {
        return content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
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
