namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>What a definition is for: <c>Standard</c> (the default) or <c>Chat</c>, one the Chat page can bind a conversation to.</summary>
/// <remarks>Denormalised onto the definition row from the graph's top-level <c>kind</c>, so a picker lists chat workflows without decrypting a graph.</remarks>
public enum GraphWorkflowDefinitionKind
{
    Standard,
    Chat
}
