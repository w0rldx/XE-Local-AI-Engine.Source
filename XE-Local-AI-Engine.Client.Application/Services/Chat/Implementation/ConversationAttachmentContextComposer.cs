namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Text;

/// <summary>One uploaded file's extracted text, ready to inline into a plain-chat turn.</summary>
internal readonly record struct AttachmentTextPart(string FileName, string Markdown);

/// <summary>
///     Assembles the synthetic plain-chat context block from a conversation's uploaded-file text. Files are labeled and
///     concatenated in order and the combined text is capped to a character budget, with a truncation notice appended
///     when the budget is exceeded. Pure and deterministic so the capping/labeling is unit-testable in isolation.
/// </summary>
internal static class ConversationAttachmentContextComposer
{
    public const string Preamble = "The user attached the following file(s) to this conversation. Use their content to answer:";
    public const string TruncationNotice = "[Attachment content was truncated to fit the context budget.]";

    /// <summary>
    ///     Returns the composed context block, or <see langword="null"/> when there is nothing to inline (no parts, or
    ///     every part's text is empty).
    /// </summary>
    public static string? Compose(IReadOnlyList<AttachmentTextPart> parts, int charBudget)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var builder = new StringBuilder();
        builder.Append(Preamble);

        var remaining = charBudget;
        var truncated = false;
        var appendedAny = false;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part.Markdown))
            {
                continue;
            }

            var header = $"\n\n[Attached document: {part.FileName}]\n";

            // Stop if even the header (plus one content char) cannot fit the remaining budget.
            if (header.Length + 1 > remaining)
            {
                truncated = true;
                break;
            }

            builder.Append(header);
            remaining -= header.Length;

            if (part.Markdown.Length > remaining)
            {
                builder.Append(part.Markdown.AsSpan(0, remaining));
                appendedAny = true;
                truncated = true;
                break;
            }

            builder.Append(part.Markdown);
            remaining -= part.Markdown.Length;
            appendedAny = true;
        }

        if (!appendedAny)
        {
            return null;
        }

        if (truncated)
        {
            builder.Append("\n\n").Append(TruncationNotice);
        }

        return builder.ToString();
    }
}
