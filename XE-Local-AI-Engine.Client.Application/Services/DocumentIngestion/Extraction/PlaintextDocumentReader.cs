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
        if (!string.IsNullOrEmpty(text))
        {
            section.Elements.Add(new IngestionDocumentParagraph(text) { Text = text });
        }

        document.Sections.Add(section);
        return document;
    }

    private static string Decode(byte[] bytes)
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
}
