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
///     Each page-targeting bookmark becomes a Markdown header at the line where its title starts, so the outline drives
///     the heading trail. A title found only at a line start of the next page moves there; any other miss keeps the
///     header before the remaining text. Runs of U+FFFD (unmapped glyphs such as leader dots) collapse to one space.
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
            List<OutlineEntry> carried = [];
            string? nextPageText = null;

            for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageText = nextPageText ?? ReadPageText(pdf, pageNumber);
                nextPageText = null;
                var segmentStart = 0;
                var searchFrom = 0;
                var fromPreviousPage = carried.Count;
                List<OutlineEntry> bookmarks = [.. carried, .. bookmarksByPage.GetValueOrDefault(pageNumber) ?? []];
                carried = [];

                for (var index = 0; index < bookmarks.Count; index++)
                {
                    var (outlineLevel, title) = bookmarks[index];

                    // Split the page at the line where the title starts, forward only. A title absent here but at a line
                    // start on the next page moves there once (outlines that target the page before the heading).
                    var anchor = FindTitleAtLineStart(pageText, title, searchFrom);
                    if (anchor >= 0)
                    {
                        AddParagraph(section, pageText[segmentStart..anchor], pageNumber);
                        segmentStart = anchor;
                        searchFrom = anchor + 1;
                    }
                    else if (index >= fromPreviousPage && pageNumber < pdf.NumberOfPages
                                                       && FindTitleAtLineStart(nextPageText ??= ReadPageText(pdf, pageNumber + 1), title, 0) >= 0)
                    {
                        carried.Add(bookmarks[index]);
                        continue;
                    }

                    var level = Math.Clamp(outlineLevel + 1, 1, MaxHeadingLevel);
                    var heading = string.Create(CultureInfo.InvariantCulture, $"{new string('#', level)} {title}");
                    section.Elements.Add(new IngestionDocumentHeader(heading)
                    {
                        Text = heading,
                        Level = level,
                        PageNumber = pageNumber
                    });
                }

                AddParagraph(section, pageText[segmentStart..], pageNumber);
            }
        }

        document.Sections.Add(section);
        return Task.FromResult(document);
    }

    private static string ReadPageText(PdfDocument pdf, int pageNumber) =>
        CollapseReplacementRuns(ContentOrderTextExtractor.GetText(pdf.GetPage(pageNumber), addDoubleNewline: true));

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
    ///     First line start at or after <paramref name="from" /> that begins with the title as whole words, or -1.
    /// </summary>
    /// <remarks>Case-insensitive; a title space matches any whitespace run, as may the gap after a title hyphen or slash.</remarks>
    internal static int FindTitleAtLineStart(string text, string title, int from)
    {
        for (var lineStart = from; lineStart < text.Length; lineStart++)
        {
            if (lineStart > 0 && text[lineStart - 1] != '\n')
            {
                continue;
            }

            if (MatchesTitleAt(text, title, SkipSpaces(text, lineStart)))
            {
                return lineStart;
            }
        }

        return -1;
    }

    private static int SkipSpaces(string text, int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t')
        {
            position++;
        }

        return position;
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

                position = SkipWhiteSpace(text, position);
                continue;
            }

            if (char.ToUpperInvariant(text[position]) != char.ToUpperInvariant(expected))
            {
                return false;
            }

            position++;
            if (expected is '-' or '/')
            {
                position = SkipWhiteSpace(text, position);
            }
        }

        return position == text.Length || char.IsWhiteSpace(text[position]);
    }

    private static int SkipWhiteSpace(string text, int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        return position;
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
