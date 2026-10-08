namespace XE_Local_AI_Engine.Client.Models;

/// <summary>Which synthetic leading context message a <see cref="ConversationMessageDto" /> is, for content-free token attribution.</summary>
public enum ConversationContextKind
{
    /// <summary>Knowledge-base grounding excerpts.</summary>
    Knowledge,

    /// <summary>Inlined attachment text, or the agent-mode pointer naming staged attachment paths.</summary>
    Attachment,

    /// <summary>A vision turn's image parts.</summary>
    Image,

    /// <summary>The compaction state and synopsis that replaces the covered history.</summary>
    Compaction
}
