namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     Builds and caches the executable <see cref="AITool" /> for each registered
///     <see cref="IClientLocalToolHandler" />.
/// </summary>
/// <remarks>
///     Each tool carries its handler's model-visible schema and description through
///     <see cref="MetadataToolFunction" />, and a high-risk handler's tool is wrapped in
///     <see cref="ApprovalRequiredAIFunction" />, so the function-invocation pipeline surfaces an approval request
///     before it runs.
/// </remarks>
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

        // Innermost guard: coerce and validate arguments against the schema, running the per-request repair loop before
        // the handler sees them. Strict unknown-property rejection is safe: these schemas enumerate their own inputs.
        function = new ToolArgumentRepairAIFunction(function, maxInvalidCalls, rejectUnknownProperties: true);

        // Backstop the output with the shared result budget, UNDER the approval gate so ApprovalRequiredAIFunction
        // stays the outermost type, which the pipeline's approval detection and the registry's type checks rely on.
        function = new BudgetedToolResultAIFunction(function, maxResultCharacters);

        return handler.RequiresApproval
            ? new ApprovalRequiredAIFunction(function)
            : function;
    }
}
