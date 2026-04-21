namespace XE_Local_AI_Engine.Client.Services.Chat;

public sealed record LocalChatInvocationSnapshot(
    Guid ConversationId,
    string SelectedModel,
    int AgentDefinitionVersion,
    bool ToolsEnabled);
