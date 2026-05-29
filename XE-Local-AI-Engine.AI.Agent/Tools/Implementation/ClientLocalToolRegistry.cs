namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

/// <summary>
///     Builds and caches the executable <see cref="AITool" /> for each registered
///     <see cref="IClientLocalToolHandler" />. Each tool carries its handler's model-visible schema + description
///     (via <see cref="MetadataToolFunction" />) and is wrapped in <see cref="ApprovalRequiredAIFunction" /> when the
///     handler is high-risk, so the framework's function-invocation pipeline surfaces an approval request before the
///     tool runs.
/// </summary>
internal sealed class ClientLocalToolRegistry : IClientLocalToolRegistry
{
    private readonly IReadOnlyDictionary<string, AITool> _tools;

    public ClientLocalToolRegistry(IEnumerable<IClientLocalToolHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var tools = new Dictionary<string, AITool>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            tools[handler.ToolName] = BuildTool(handler);
        }

        _tools = tools;
    }

    public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return _tools.TryGetValue(toolName, out tool);
    }

    private static AITool BuildTool(IClientLocalToolHandler handler)
    {
        var schema = MetadataToolFunction.ParseSchema(handler.ParameterSchema);
        AIFunction function = new MetadataToolFunction(handler.ToolName,
            handler.Description,
            schema,
            handler.ExecuteAsync);

        return handler.RequiresApproval
            ? new ApprovalRequiredAIFunction(function)
            : function;
    }
}
