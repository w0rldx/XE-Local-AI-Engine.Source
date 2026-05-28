namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
/// Shared conversation-title derivation. The platform path (Phase 1.2) synthesizes a title from the first user
/// turn in the runtime package's conversation context; this keeps that rule identical to the local
/// last-message preview truncation (see <c>NodeChatPersistenceService.Preview</c>). See
/// Plans/schema-contract-sheet.md §3.
/// </summary>
public static class NodeChatTitle
{
    private const int MaxLength = 120;

    /// <summary>
    /// Derives a conversation title from a user message's content: trimmed and clamped to 120 chars. Returns
    /// null for null/whitespace input (callers fall back to "Untitled conversation" on the client).
    /// </summary>
    public static string? FromUserContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength];
    }
}
