namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

using System.Text;
using Microsoft.Extensions.DataIngestion;
using UtfUnknown;

/// <summary>
///     Reads plaintext, Markdown, structured-text, and source-code files. The byte encoding is detected with
///     UTF.Unknown so BOM-less legacy text (e.g. Windows-1252) decodes correctly; UTF-8 is the fallback.
/// </summary>
internal sealed class PlaintextDocumentReader : IngestionDocumentReader
{
    static PlaintextDocumentReader()
    {
        // UTF.Unknown can report legacy code-page encodings (e.g. windows-1252). Registering the provider lets the
        // returned Encoding instances resolve at runtime on .NET. RegisterProvider is process-global and idempotent.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Extensions handled by this reader, matched case-insensitively (leading dot included).</summary>
    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
    [
        ".txt", ".text", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonc", ".log",
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".java", ".go", ".rs",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh", ".html", ".htm", ".xml", ".xaml",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".env",
        ".sh", ".bash", ".zsh", ".ps1", ".bat", ".sql", ".css", ".scss", ".sass", ".less",
        ".rb", ".php", ".kt", ".kts", ".swift", ".scala", ".pl", ".lua", ".r", ".vb", ".fs", ".fsx",
        ".gradle", ".dockerfile", ".gitignore", ".editorconfig"
    ];

    public override async Task<IngestionDocument> ReadAsync(Stream source, string? identifier, string? mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var document = new IngestionDocument(identifier ?? "document");
        var section = new IngestionDocumentSection();

        var text = Decode(bytes);
        if (IsMarkdown(mediaType))
        {
            AddMarkdownElements(section, text);
        }
        else
        {
            AddLosslessParagraphs(section, text);
        }

        document.Sections.Add(section);
        return document;
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var detection = CharsetDetector.DetectFromBytes(bytes);
        var encoding = detection.Detected?.Encoding ?? Encoding.UTF8;

        // Strip a leading BOM (U+FEFF) if the decoder preserved it, so it does not leak into the extracted text.
        return encoding.GetString(bytes).TrimStart('\uFEFF');
    }

    private static bool IsMarkdown(string? mediaType)
    {
        return string.Equals(mediaType, ".md", StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType, ".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddMarkdownElements(IngestionDocumentSection section, string text)
    {
        var insideFence = false;
        foreach (var block in EnumerateLosslessBlocks(text))
        {
            var headingLevel = insideFence ? null : TryGetMarkdownHeadingLevel(block);
            if (headingLevel is int level)
            {
                section.Elements.Add(new IngestionDocumentHeader(block)
                {
                    Text = block,
                    Level = level
                });
            }
            else
            {
                AddParagraph(section, block);
            }

            UpdateFenceState(block, ref insideFence);
        }
    }

    private static void AddLosslessParagraphs(IngestionDocumentSection section, string text)
    {
        foreach (var block in EnumerateLosslessBlocks(text))
        {
            AddParagraph(section, block);
        }
    }

    private static void AddParagraph(IngestionDocumentSection section, string text)
    {
        section.Elements.Add(new IngestionDocumentParagraph(text)
        {
            Text = text
        });
    }

    // The serializer restores exactly two LF characters between elements. Splitting only at that exact sequence keeps
    // the flattened output byte-for-text compatible; CRLF and unusual whitespace stay in a single lossless element.
    private static IEnumerable<string> EnumerateLosslessBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var separator = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            if (separator <= start || separator + 2 >= text.Length)
            {
                yield return text[start..];
                yield break;
            }

            yield return text[start..separator];
            start = separator + 2;
        }
    }

    private static int? TryGetMarkdownHeadingLevel(string block)
    {
        if (block.IndexOf('\n', StringComparison.Ordinal) < 0)
        {
            var offset = 0;
            while (offset < block.Length && offset < 3 && block[offset] == ' ')
            {
                offset++;
            }

            var markerStart = offset;
            while (offset < block.Length && offset - markerStart < 6 && block[offset] == '#')
            {
                offset++;
            }

            var level = offset - markerStart;
            if (level > 0 && (offset == block.Length || char.IsWhiteSpace(block[offset])))
            {
                return level;
            }

            return null;
        }

        var lines = block.Split('\n');
        if (lines.Length != 2 || string.IsNullOrWhiteSpace(lines[0]))
        {
            return null;
        }

        var underline = lines[1].AsSpan().Trim();
        if (underline.Length == 0)
        {
            return null;
        }

        var marker = underline[0];
        if (marker is not ('=' or '-'))
        {
            return null;
        }

        foreach (var character in underline)
        {
            if (character != marker)
            {
                return null;
            }
        }

        return marker == '=' ? 1 : 2;
    }

    private static void UpdateFenceState(string block, ref bool insideFence)
    {
        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                insideFence = !insideFence;
            }
        }
    }
}
