namespace XE_Local_AI_Engine.Client.Services.Mcp;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>Shared fail-closed policy for the only binding allowed to receive an opaque workspace.</summary>
internal static class McpExecutionBindingPolicy
{
    private static readonly HashSet<string> WorkspaceToolNames =
    [
        "list_files",
        "read_file",
        "search_text"
    ];

    public static bool IsExactReadOnlyWorkspaceCoder(McpExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.AgentDefinitionId is not null
               && binding.AllowedTools.Count == WorkspaceToolNames.Count
               && binding.AllowedTools.Select(static tool => tool.Name).Distinct(StringComparer.Ordinal).Count() == WorkspaceToolNames.Count
               && binding.AllowedTools.All(static tool => WorkspaceToolNames.Contains(tool.Name)
                                                          && tool.Location == ToolLocation.ClientLocal
                                                          && tool.Category == ToolCategory.ReadLocal
                                                          && !tool.RequiresApproval);
    }
}
