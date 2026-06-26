namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.DataIngestion;

/// <summary>
///     Reads text from Word (.docx) files with the Open XML SDK (pure-managed). Plain text is the floor; light
///     Markdown is layered on best-effort: heading styles map to <c>#</c> levels, list paragraphs to <c>-</c>
///     bullets, bold/italic runs to <c>**</c>/<c>*</c>, and tables to GitHub-style Markdown tables.
/// </summary>
internal sealed class DocxDocumentReader : IngestionDocumentReader
{
    private const int MaxHeadingLevel = 6;

    public override Task<IngestionDocument> ReadAsync(Stream source, string? identifier, string? mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var document = new IngestionDocument(identifier ?? "document");
        var section = new IngestionDocumentSection();

        using (var word = WordprocessingDocument.Open(source, isEditable: false))
        {
            var body = word.MainDocumentPart?.Document?.Body;
            if (body is not null)
            {
                foreach (var block in body.ChildElements)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var element = ConvertBlock(block);
                    if (element is not null)
                    {
                        section.Elements.Add(element);
                    }
                }
            }
        }

        document.Sections.Add(section);
        return Task.FromResult(document);
    }

    private static IngestionDocumentElement? ConvertBlock(OpenXmlElement block)
    {
        return block switch
        {
            Paragraph paragraph => ConvertParagraph(paragraph),
            Table table => ConvertTable(table),
            _ => null
        };
    }

    private static IngestionDocumentElement? ConvertParagraph(Paragraph paragraph)
    {
        var inline = RenderInline(paragraph);
        if (string.IsNullOrWhiteSpace(inline))
        {
            return null;
        }

        var headingLevel = ResolveHeadingLevel(paragraph);
        if (headingLevel is int level)
        {
            var heading = string.Create(CultureInfo.InvariantCulture, $"{new string('#', level)} {inline}");
            return new IngestionDocumentHeader(heading) { Text = heading, Level = level };
        }

        if (IsListItem(paragraph))
        {
            var bullet = "- " + inline;
            return new IngestionDocumentParagraph(bullet) { Text = bullet };
        }

        return new IngestionDocumentParagraph(inline) { Text = inline };
    }

    private static string RenderInline(Paragraph paragraph)
    {
        var builder = new StringBuilder();
        foreach (var run in paragraph.Elements<Run>())
        {
            var text = run.InnerText;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var properties = run.RunProperties;
            var bold = properties?.Bold is { } b && (b.Val?.Value ?? true);
            var italic = properties?.Italic is { } i && (i.Val?.Value ?? true);

            if (bold && italic)
            {
                builder.Append("***").Append(text).Append("***");
            }
            else if (bold)
            {
                builder.Append("**").Append(text).Append("**");
            }
            else if (italic)
            {
                builder.Append('*').Append(text).Append('*');
            }
            else
            {
                builder.Append(text);
            }
        }

        // Fall back to the paragraph's raw text when it has no run children (e.g. fields/SDT content).
        return builder.Length == 0 ? paragraph.InnerText : builder.ToString();
    }

    private static int? ResolveHeadingLevel(Paragraph paragraph)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (string.IsNullOrEmpty(styleId))
        {
            return null;
        }

        if (styleId.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var normalized = styleId.Replace(" ", string.Empty, StringComparison.Ordinal);
        const string headingPrefix = "Heading";
        if (normalized.StartsWith(headingPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(normalized.AsSpan(headingPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            && level >= 1)
        {
            return Math.Min(level, MaxHeadingLevel);
        }

        return null;
    }

    private static bool IsListItem(Paragraph paragraph)
    {
        return paragraph.ParagraphProperties?.NumberingProperties is not null;
    }

    private static IngestionDocumentElement? ConvertTable(Table table)
    {
        var rendered = new List<List<string>>();
        var columnCount = 0;

        foreach (var row in table.Elements<TableRow>())
        {
            var cells = row.Elements<TableCell>()
                           .Select(static cell => SanitizeCell(cell.InnerText))
                           .ToList();
            columnCount = Math.Max(columnCount, cells.Count);
            rendered.Add(cells);
        }

        if (columnCount == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        AppendRow(builder, rendered[0], columnCount);

        builder.Append('|');
        for (var column = 0; column < columnCount; column++)
        {
            builder.Append(" --- |");
        }

        builder.Append('\n');

        foreach (var row in rendered.Skip(1))
        {
            AppendRow(builder, row, columnCount);
        }

        var markdown = builder.ToString().TrimEnd('\n');
        return new IngestionDocumentParagraph(markdown) { Text = markdown };
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells, int columnCount)
    {
        builder.Append('|');
        for (var column = 0; column < columnCount; column++)
        {
            var value = column < cells.Count ? cells[column] : string.Empty;
            builder.Append(' ').Append(value).Append(" |");
        }

        builder.Append('\n');
    }

    private static string SanitizeCell(string text)
    {
        return text.Replace('\r', ' ')
                   .Replace('\n', ' ')
                   .Replace("|", "\\|", StringComparison.Ordinal)
                   .Trim();
    }
}
