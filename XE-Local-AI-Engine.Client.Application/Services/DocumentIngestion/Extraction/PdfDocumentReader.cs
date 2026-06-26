namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

using Microsoft.Extensions.DataIngestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

/// <summary>
///     Reads text from PDF files with PdfPig (pure-managed, Apache-2.0). Each page's text is extracted in reading
///     order and emitted as a paragraph; image-only / scanned PDFs yield no text (OCR is out of scope).
/// </summary>
internal sealed class PdfDocumentReader : IngestionDocumentReader
{
    public override Task<IngestionDocument> ReadAsync(Stream source, string? identifier, string? mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var document = new IngestionDocument(identifier ?? "document");
        var section = new IngestionDocumentSection();

        using (var pdf = PdfDocument.Open(source))
        {
            foreach (var page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageText = ContentOrderTextExtractor.GetText(page, addDoubleNewline: true);
                if (string.IsNullOrWhiteSpace(pageText))
                {
                    continue;
                }

                section.Elements.Add(new IngestionDocumentParagraph(pageText) { Text = pageText, PageNumber = page.Number });
            }
        }

        document.Sections.Add(section);
        return Task.FromResult(document);
    }
}
