namespace XE_Local_AI_Engine.Client.Services.Capacity;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Capacity.Tools;

/// <summary>Pure binding and child-tool policies shared by the sub-agent spawn paths.</summary>
internal static class SubAgentSpawnPolicy
{
    public static bool HasExactlyOneBinding(SubAgentSpawnRequest request)
    {
        var hasKey = !string.IsNullOrWhiteSpace(request.SubAgentKey);
        var hasModel = !string.IsNullOrWhiteSpace(request.ModelId);
        return hasKey ^ hasModel;
    }

    public static bool HasExactCoderToolNames(IEnumerable<string> names)
    {
        var distinctNames = names.ToHashSet(StringComparer.Ordinal);
        return distinctNames.Count == 3
               && distinctNames.Contains("list_files")
               && distinctNames.Contains("read_file")
               && distinctNames.Contains("search_text");
    }

    public static IReadOnlyList<AITool> ToOfferPlaceholders(IReadOnlyList<AllowedToolDto> offered)
    {
        return
        [
            .. offered
               .Where(static tool => tool.Location == ToolLocation.ClientLocal)
               .Select(static tool => InvocationToolBridge.CreateOfferPlaceholder(tool.Name, tool.RequiresApproval))
        ];
    }

    public static IList<AITool>? RemoveUnsupportedChildTools(IList<AITool> offeredExecutables,
        out IReadOnlyList<string> droppedApprovalToolNames)
    {
        var curated = new List<AITool>(offeredExecutables.Count);
        List<string>? dropped = null;
        foreach (var tool in offeredExecutables)
        {
            if (string.Equals(tool.Name, SpawnSubAgentToolDefinition.ToolName, StringComparison.Ordinal))
            {
                continue;
            }

            if (tool is ApprovalRequiredAIFunction)
            {
                (dropped ??= []).Add(tool.Name);
                continue;
            }

            curated.Add(tool);
        }

        droppedApprovalToolNames = dropped ?? [];
        return curated.Count > 0 ? curated : null;
    }
}
