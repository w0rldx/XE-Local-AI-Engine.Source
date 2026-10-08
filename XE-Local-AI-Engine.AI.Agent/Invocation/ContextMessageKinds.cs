namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     The <see cref="Microsoft.Extensions.AI.ChatMessage.AdditionalProperties" /> key and values that tag a synthetic
///     leading context message by kind, so the provider-boundary hop can attribute its tokens without reading content.
/// </summary>
/// <remarks>
///     Set by the invocation runner when it renders the conversation context; untagged messages count as conversation.
///     The values are fixed identifiers, never content.
/// </remarks>
public static class ContextMessageKinds
{
    public const string Key = "xe.context.kind";

    public const string Knowledge = "knowledge";

    public const string Attachment = "attachment";

    public const string Image = "image";

    public const string Compaction = "compaction";
}
