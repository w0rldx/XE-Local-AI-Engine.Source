namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Shared conversation-title derivation: trim and clamp the first non-empty user message to 120 characters, or
///     NULL when there is none, and the client then shows "Untitled conversation".
/// </summary>
/// <remarks>
///     The platform path synthesizes its title from the first user turn in the runtime package's context, and this
///     keeps that rule identical to the local last-message preview truncation.
/// </remarks>
public static class NodeChatTitle
{
    private const int MaxLength = 120;

    /// <summary>
    ///     Derives a conversation title from a user message's content: trimmed and clamped to 120 chars. Returns
    ///     null for null/whitespace input (callers fall back to "Untitled conversation" on the client).
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
