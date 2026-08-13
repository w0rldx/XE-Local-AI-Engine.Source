namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>Version stamps that make parser/chunker changes explicit cache and reindex boundaries.</summary>
public static class KnowledgeIndexVersions
{
    public const string Parser = "structured-v2";
    public const string Chunker = "header-window-v2";
}
