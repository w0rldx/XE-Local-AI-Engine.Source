namespace XE_Local_AI_Engine.Client.Services.WebAccess;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     The built-in web tools as offer descriptors, merged into the whole offer while <c>WebAccessEnabled</c> is on.
/// </summary>
/// <remarks>
///     <see cref="ToolCategory.Network" /> is the honest category: each call reaches the public internet.
///     Both tools carry a structural approval flag, as <c>ask_user</c> does, which the unattended paths strip.
/// </remarks>
public static class WebAccessToolCatalog
{
    internal static readonly IReadOnlyList<LocalChatToolDescriptor> Descriptors =
    [
        new()
        {
            Name = WebFetchToolDefinition.ToolName,
            Description = WebFetchToolDefinition.Description,
            ParameterSchema = WebFetchToolDefinition.ParameterSchema,
            RequiresApproval = true,
            Category = ToolCategory.Network
        },
        new()
        {
            Name = WebSearchToolDefinition.ToolName,
            Description = WebSearchToolDefinition.Description,
            ParameterSchema = WebSearchToolDefinition.ParameterSchema,
            RequiresApproval = true,
            Category = ToolCategory.Network
        }
    ];

    /// <summary>Whether <paramref name="toolName" /> is one of the two web tools: the ONE predicate the review gate, every strip and the MCP mapping match on.</summary>
    public static bool IsWebTool(string? toolName) =>
        string.Equals(toolName, WebFetchToolDefinition.ToolName, StringComparison.Ordinal)
        || string.Equals(toolName, WebSearchToolDefinition.ToolName, StringComparison.Ordinal);
}
