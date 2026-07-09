namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;

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

    public ClientLocalToolRegistry(IEnumerable<IClientLocalToolHandler> handlers, IOptions<AgentToolPipelineOptions> pipelineOptions)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(pipelineOptions);

        var maxResultCharacters = pipelineOptions.Value.MaxToolResultCharacters;
        var maxInvalidCalls = pipelineOptions.Value.MaxConsecutiveInvalidToolCallsPerTool;
        var tools = new Dictionary<string, AITool>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            tools[handler.ToolName] = BuildTool(handler, maxResultCharacters, maxInvalidCalls);
        }

        _tools = tools;
    }

    public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return _tools.TryGetValue(toolName, out tool);
    }

    private static AITool BuildTool(IClientLocalToolHandler handler, int maxResultCharacters, int maxInvalidCalls)
    {
        var schema = MetadataToolFunction.ParseSchema(handler.ParameterSchema);
        AIFunction function = new MetadataToolFunction(handler.ToolName,
            handler.Description,
            schema,
            handler.ExecuteAsync);

        // Innermost guard: coerce + validate the model's arguments against this tool's schema and run the per-request
        // repair loop before the handler ever sees them, so a malformed call returns actionable guidance instead of a
        // throw. Sits under the budget and approval wrappers, which stay transparent to it.
        function = new ToolArgumentRepairAIFunction(function, maxInvalidCalls);

        // Backstop the handler's output with the shared result budget. This wraps the executable UNDER the approval
        // gate so ApprovalRequiredAIFunction stays the outermost type (the pipeline's approval detection and the
        // registry's own type checks rely on that), while still bounding what the tool emits into chat history.
        function = new BudgetedToolResultAIFunction(function, maxResultCharacters);

        return handler.RequiresApproval
            ? new ApprovalRequiredAIFunction(function)
            : function;
    }
}
