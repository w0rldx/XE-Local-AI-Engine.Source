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
    private readonly IReadOnlyDictionary<string, IClientLocalToolPreflight> _preflights;
    private readonly IReadOnlyDictionary<string, AITool> _refusingTools;

    public ClientLocalToolRegistry(IEnumerable<IClientLocalToolHandler> handlers, IOptions<AgentToolPipelineOptions> pipelineOptions)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(pipelineOptions);

        var maxResultCharacters = pipelineOptions.Value.MaxToolResultCharacters;
        var maxInvalidCalls = pipelineOptions.Value.MaxConsecutiveInvalidToolCallsPerTool;
        var tools = new Dictionary<string, AITool>(StringComparer.Ordinal);
        var preflights = new Dictionary<string, IClientLocalToolPreflight>(StringComparer.Ordinal);
        var refusingTools = new Dictionary<string, AITool>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            tools[handler.ToolName] = BuildTool(handler, maxResultCharacters, maxInvalidCalls);
            if (handler is IClientLocalToolPreflight preflight)
            {
                preflights[handler.ToolName] = preflight;
                refusingTools[handler.ToolName] = BuildRefusingTool(handler, preflight);
            }
        }

        _tools = tools;
        _preflights = preflights;
        _refusingTools = refusingTools;
    }

    public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return _tools.TryGetValue(toolName, out tool);
    }

    public async ValueTask<AITool?> ResolvePreflightRefusalAsync(string toolName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return _preflights.TryGetValue(toolName, out var preflight) && await preflight.PreflightAsync(cancellationToken).ConfigureAwait(false) is not null
            ? _refusingTools[toolName]
            : null;
    }

    /// <summary>The approval-free variant handed out for a turn whose preflight refused. It never calls the handler.</summary>
    /// <remarks>
    ///     It asks the preflight again and answers with that refusal, so a node that became able to run the tool mid-turn still gets
    ///     no unapproved call.
    /// </remarks>
    private static AIFunction BuildRefusingTool(IClientLocalToolHandler handler, IClientLocalToolPreflight preflight)
    {
        var toolName = handler.ToolName;
        return new MetadataToolFunction(toolName,
            handler.Description,
            MetadataToolFunction.ParseSchema(handler.ParameterSchema),
            async (_, cancellationToken) => ToolFailureText.FormatNotRun(ToolFailureText.ToolReportedCode,
                await preflight.PreflightAsync(cancellationToken).ConfigureAwait(false)
                ?? $"{toolName} was not run: this node can run it now, which needs approval. Call it again in a new message."));
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
