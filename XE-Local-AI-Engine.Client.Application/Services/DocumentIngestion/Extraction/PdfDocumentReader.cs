namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.DataIngestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Outline;

/// <summary>
///     Reads text from PDF files with PdfPig (pure-managed, Apache-2.0). Each page's text is extracted in reading
///     order and emitted as a paragraph; image-only / scanned PDFs yield no text (OCR is out of scope).
/// </summary>
/// <remarks>
///     When the PDF has an outline (bookmarks), each bookmark that targets a page becomes a Markdown header placed
///     at the line of the page text where its title starts (the page splits there; a title that is not found at a line
///     start, or only before the previous bookmark's position, keeps the header before the remaining text), so the
///     outline drives the chunker's heading trail. Runs of U+FFFD (glyphs with no
///     Unicode mapping, typically table-of-contents leader dots) collapse to a single space.
/// </remarks>
internal sealed class PdfDocumentReader : IngestionDocumentReader
{
    private const int MaxHeadingLevel = 6;

    public override Task<IngestionDocument> ReadAsync(Stream source, string? identifier, string? mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var document = new IngestionDocument(identifier ?? "document");
        var section = new IngestionDocumentSection();

        using (var pdf = PdfDocument.Open(source))
        {
            var bookmarksByPage = ReadBookmarksByPage(pdf);

            foreach (var page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageText = CollapseReplacementRuns(ContentOrderTextExtractor.GetText(page, addDoubleNewline: true));
                var segmentStart = 0;
                var searchFrom = 0;

                if (bookmarksByPage.TryGetValue(page.Number, out var bookmarks))
                {
                    foreach (var (outlineLevel, title) in bookmarks)
                    {
                        // Split the page at the line where the title starts, forward only; a title that is absent or
                        // only found before the previous anchor keeps the header at the current position.
                        var anchor = FindTitleAtLineStart(pageText, title, searchFrom);
                        if (anchor >= 0)
                        {
                            AddParagraph(section, pageText[segmentStart..anchor], page.Number);
                            segmentStart = anchor;
                            searchFrom = anchor + 1;
                        }

                        var level = Math.Clamp(outlineLevel + 1, 1, MaxHeadingLevel);
                        var heading = string.Create(CultureInfo.InvariantCulture, $"{new string('#', level)} {title}");
                        section.Elements.Add(new IngestionDocumentHeader(heading)
                        {
                            Text = heading,
                            Level = level,
                            PageNumber = page.Number
                        });
                    }
                }

                AddParagraph(section, pageText[segmentStart..], page.Number);
            }
        }

        document.Sections.Add(section);
        return Task.FromResult(document);
    }

    // Page-targeting outline entries grouped by page, in outline order, titles whitespace-collapsed; URI, external and
    // embedded bookmarks (no page in this document) and empty titles are skipped.
    private static Dictionary<int, List<OutlineEntry>> ReadBookmarksByPage(PdfDocument pdf)
    {
        var byPage = new Dictionary<int, List<OutlineEntry>>();
        if (!pdf.TryGetBookmarks(out var bookmarks) || bookmarks is null)
        {
            return byPage;
        }

        foreach (var node in bookmarks.GetNodes().OfType<DocumentBookmarkNode>())
        {
            var title = string.Join(' ', CollapseReplacementRuns(node.Title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length == 0)
            {
                continue;
            }

            if (!byPage.TryGetValue(node.PageNumber, out var list))
            {
                list = [];
                byPage[node.PageNumber] = list;
            }

            list.Add(new OutlineEntry(node.Level, title));
        }

        return byPage;
    }

    private readonly record struct OutlineEntry(int Level, string Title);

    private static void AddParagraph(IngestionDocumentSection section, string text, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        section.Elements.Add(new IngestionDocumentParagraph(text)
        {
            Text = text,
            PageNumber = pageNumber
        });
    }

    /// <summary>
    ///     First line start at or after <paramref name="from" /> whose line begins with the title as whole words
    ///     (case-insensitive, any whitespace run matches a title space), or -1.
    /// </summary>
    internal static int FindTitleAtLineStart(string text, string title, int from)
    {
        for (var lineStart = from; lineStart < text.Length; lineStart++)
        {
            if (lineStart > 0 && text[lineStart - 1] != '\n')
            {
                continue;
            }

            var position = lineStart;
            while (position < text.Length && text[position] is ' ' or '\t')
            {
                position++;
            }

            if (MatchesTitleAt(text, title, position))
            {
                return lineStart;
            }
        }

        return -1;
    }

    private static bool MatchesTitleAt(string text, string title, int position)
    {
        foreach (var expected in title)
        {
            if (position >= text.Length)
            {
                return false;
            }

            if (expected == ' ')
            {
                if (!char.IsWhiteSpace(text[position]))
                {
                    return false;
                }

                while (position < text.Length && char.IsWhiteSpace(text[position]))
                {
                    position++;
                }

                continue;
            }

            if (char.ToUpperInvariant(text[position]) != char.ToUpperInvariant(expected))
            {
                return false;
            }

            position++;
        }

        return position == text.Length || char.IsWhiteSpace(text[position]);
    }

    // Each run of U+FFFD becomes one space, so unmapped leader dots no longer reach chunks or embeddings.
    internal static string CollapseReplacementRuns(string text)
    {
        if (!text.Contains('\uFFFD', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\uFFFD')
            {
                _ = builder.Append(text[index]);
            }
            else if (index == 0 || text[index - 1] != '\uFFFD')
            {
                _ = builder.Append(' ');
            }
        }

        return builder.ToString();
    }
}
