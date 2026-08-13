namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.DataIngestion;

/// <summary>
///     Deterministically extracts visible text and heading boundaries from HTML without executing markup. Script and
///     style elements are discarded with their contents; no DOM or browser behavior participates in extraction.
/// </summary>
internal sealed class HtmlDocumentReader : IngestionDocumentReader
{
    public override async Task<IngestionDocument> ReadAsync(Stream source,
        string? identifier,
        string? mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var html = PlaintextDocumentReader.Decode(buffer.ToArray());

        var document = new IngestionDocument(identifier ?? "document");
        var section = new IngestionDocumentSection();
        ExtractElements(html, section);
        document.Sections.Add(section);
        return document;
    }

    private static void ExtractElements(string html, IngestionDocumentSection section)
    {
        var text = new StringBuilder();
        int? headingLevel = null;
        var preserveWhitespace = false;
        var position = 0;

        while (position < html.Length)
        {
            if (html[position] != '<')
            {
                var nextTag = html.IndexOf('<', position);
                var end = nextTag < 0 ? html.Length : nextTag;
                _ = text.Append(html, position, end - position);
                position = end;
                continue;
            }

            if (html.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = html.IndexOf("-->", position + 4, StringComparison.Ordinal);
                position = commentEnd < 0 ? html.Length : commentEnd + 3;
                continue;
            }

            var tagEnd = FindTagEnd(html, position + 1);
            if (tagEnd < 0)
            {
                _ = text.Append(html.AsSpan(position));
                break;
            }

            var (tagName, isClosingTag) = ParseTag(html.AsSpan(position + 1, tagEnd - position - 1));
            position = tagEnd + 1;
            if (tagName.Length == 0)
            {
                continue;
            }

            if (!isClosingTag && (tagName.Equals("script", StringComparison.OrdinalIgnoreCase)
                                  || tagName.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                position = SkipRawElement(html, position, tagName);
                continue;
            }

            if (TryGetHeadingLevel(tagName, out var level))
            {
                if (!isClosingTag)
                {
                    Flush(section, text, headingLevel, preserveWhitespace);
                    headingLevel = level;
                }
                else if (headingLevel is not null)
                {
                    Flush(section, text, headingLevel, preserveWhitespace);
                    headingLevel = null;
                }

                continue;
            }

            if (tagName.Equals("pre", StringComparison.OrdinalIgnoreCase))
            {
                if (!isClosingTag)
                {
                    Flush(section, text, headingLevel, preserveWhitespace);
                    preserveWhitespace = true;
                }
                else
                {
                    Flush(section, text, headingLevel, preserveWhitespace);
                    preserveWhitespace = false;
                }

                continue;
            }

            if (IsBlockBoundary(tagName) || tagName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                Flush(section, text, headingLevel, preserveWhitespace);
            }
        }

        Flush(section, text, headingLevel, preserveWhitespace);
    }

    private static void Flush(IngestionDocumentSection section,
        StringBuilder buffer,
        int? headingLevel,
        bool preserveWhitespace)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var decoded = WebUtility.HtmlDecode(buffer.ToString());
        buffer.Clear();
        var visible = preserveWhitespace ? decoded.Trim('\r', '\n') : CollapseWhitespace(decoded);
        if (string.IsNullOrWhiteSpace(visible))
        {
            return;
        }

        if (headingLevel is int level)
        {
            var markdown = string.Create(CultureInfo.InvariantCulture, $"{new string('#', level)} {visible}");
            section.Elements.Add(new IngestionDocumentHeader(markdown)
            {
                Text = markdown,
                Level = level
            });
            return;
        }

        section.Elements.Add(new IngestionDocumentParagraph(visible)
        {
            Text = visible
        });
    }

    private static string CollapseWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    _ = result.Append(' ');
                    pendingSpace = false;
                }

                _ = result.Append(character);
            }
        }

        return result.ToString();
    }

    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var index = start; index < html.Length; index++)
        {
            var character = html[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static (string Name, bool IsClosing) ParseTag(ReadOnlySpan<char> tag)
    {
        tag = tag.Trim();
        var isClosing = tag.Length > 0 && tag[0] == '/';
        if (isClosing)
        {
            tag = tag[1..].TrimStart();
        }

        var length = 0;
        while (length < tag.Length && (char.IsLetterOrDigit(tag[length]) || tag[length] is '-' or ':'))
        {
            length++;
        }

        return (tag[..length].ToString(), isClosing);
    }

    private static int SkipRawElement(string html, int position, string tagName)
    {
        var closingStart = html.IndexOf("</" + tagName, position, StringComparison.OrdinalIgnoreCase);
        if (closingStart < 0)
        {
            return html.Length;
        }

        var closingEnd = FindTagEnd(html, closingStart + 2 + tagName.Length);
        return closingEnd < 0 ? html.Length : closingEnd + 1;
    }

    private static bool TryGetHeadingLevel(string tagName, out int level)
    {
        level = 0;
        return tagName.Length == 2
               && (tagName[0] is 'h' or 'H')
               && int.TryParse(tagName.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out level)
               && level is >= 1 and <= 6;
    }

    private static bool IsBlockBoundary(string tagName)
    {
        return tagName.Equals("p", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("div", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("section", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("article", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("main", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("header", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("footer", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("aside", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("nav", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("li", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("tr", StringComparison.OrdinalIgnoreCase)
               || tagName.Equals("hr", StringComparison.OrdinalIgnoreCase);
    }
}
