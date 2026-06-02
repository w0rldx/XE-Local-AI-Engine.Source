namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Enumerates the persisted classification of a local model. Drives the chat picker filter (only
///     <see cref="Chat" /> is offered) and the Model Management type display. The numeric values are persisted,
///     so existing values must never be renumbered — future kinds (Vision, Reranker, CodeCompletion, Moderation)
///     append new values only.
/// </summary>
public enum ModelKind
{
    Unknown = 0,
    Chat = 1,
    Embedding = 2

    // Reserved for future growth (append only, do NOT renumber): Vision, Reranker, CodeCompletion, Moderation.
}
