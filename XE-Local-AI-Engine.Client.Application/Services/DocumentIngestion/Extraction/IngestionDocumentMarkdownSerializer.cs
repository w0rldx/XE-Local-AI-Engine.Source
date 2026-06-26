namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

using Microsoft.Extensions.DataIngestion;

/// <summary>
///     Flattens an <see cref="IngestionDocument"/> produced by a reader into a single Markdown/plaintext string.
///     Each content element carries its already-rendered Markdown in <see cref="IngestionDocumentElement.Text"/>;
///     non-empty elements are joined in document order with a blank line between them.
/// </summary>
internal static class IngestionDocumentMarkdownSerializer
{
    public static string Serialize(IngestionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var blocks = document.EnumerateContent()
                             .Select(static element => element.Text)
                             .Where(static text => !string.IsNullOrEmpty(text));

        return string.Join("\n\n", blocks);
    }
}
