namespace XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Live-reading implementation of <see cref="ICustomToolCatalog" />. Reads the enabled, acknowledged custom tools
///     from the node store on every call and builds each one's executable through the SAME wrapper stack the built-in
///     and MCP tools use — arg-repair (strict, the schema fully enumerates inputs) under a result budget under a forced
///     <see cref="ApprovalRequiredAIFunction" />. The approval wrap is unconditional: it does not read a stored flag, so
///     no per-agent override can strip it and the sub-agent/scheduler filter always sees a gated tool.
/// </summary>
internal sealed class CustomToolCatalog : ICustomToolCatalog
{
    private readonly ICustomToolStore _store;
    private readonly IReadOnlyDictionary<CustomToolKind, ICustomToolExecutor> _executors;
    private readonly int _maxResultCharacters;
    private readonly int _maxInvalidCalls;
    private readonly ILogger<CustomToolCatalog> _logger;

    public CustomToolCatalog(ICustomToolStore store,
        IEnumerable<ICustomToolExecutor> executors,
        IOptions<AgentToolPipelineOptions> pipelineOptions,
        ILogger<CustomToolCatalog> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(pipelineOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _executors = executors.ToDictionary(static executor => executor.Kind);
        _maxResultCharacters = pipelineOptions.Value.MaxToolResultCharacters;
        _maxInvalidCalls = pipelineOptions.Value.MaxConsecutiveInvalidToolCallsPerTool;
    }

    public async Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var descriptors = new List<LocalChatToolDescriptor>();
        foreach (var tool in tools)
        {
            if (!IsOfferable(tool))
            {
                continue;
            }

            if (TryBuildSchema(tool, out var schema))
            {
                descriptors.Add(BuildDescriptor(tool, schema));
            }
        }

        return descriptors;
    }

    public async Task<AITool?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var tools = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var tool = tools.FirstOrDefault(candidate => IsOfferable(candidate)
                                                     && string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (tool is null)
        {
            return null;
        }

        if (!_executors.TryGetValue(tool.Kind, out var executor))
        {
            _logger.LogWarning("Custom tool {ToolName} has no executor for kind {Kind}.", tool.Name, tool.Kind);
            return null;
        }

        if (!TryBuildSchema(tool, out var schema))
        {
            return null;
        }

        return BuildExecutable(tool, schema, executor);
    }

    // Only an enabled AND server-acknowledged tool is ever offered or resolved — a disabled tool stays authored but dark,
    // and the danger acknowledgement is a hard execution gate, not just an authoring one.
    private static bool IsOfferable(CustomToolRecord tool)
    {
        return tool.Enabled && tool.Acknowledged && CustomToolValidation.IsValidToolName(tool.Name);
    }

    private bool TryBuildSchema(CustomToolRecord tool, out string schema)
    {
        try
        {
            var parameters = CustomToolConfigParser.ParseParameters(tool.ParametersJson);
            schema = CustomToolSchemaCompiler.Compile(tool.Mode, parameters);
            return true;
        }
        catch (CustomToolConfigurationException exception)
        {
            _logger.LogWarning("Skipping custom tool {ToolName}: {Reason}", tool.Name, exception.Message);
            schema = string.Empty;
            return false;
        }
    }

    private static LocalChatToolDescriptor BuildDescriptor(CustomToolRecord tool, string schema)
    {
        return new LocalChatToolDescriptor(tool.Name,
            tool.Description,
            schema,
            RequiresApproval: true,
            CategoryFor(tool.Kind));
    }

    private AITool BuildExecutable(CustomToolRecord tool, string schema, ICustomToolExecutor executor)
    {
        var parsedSchema = MetadataToolFunction.ParseSchema(schema);

        // The record is captured at resolve time (a live store read), so the executor always runs against the tool as it
        // stands this turn. Executor guards + secret-scrubbing live below this seam.
        AIFunction function = new MetadataToolFunction(tool.Name,
            tool.Description,
            parsedSchema,
            (jsonArguments, cancellationToken) => executor.ExecuteAsync(tool, jsonArguments, cancellationToken));

        // Strict unknown-property rejection: a custom tool's compiled schema is a closed object that fully enumerates
        // its inputs, so an undeclared key is a hallucination to bounce.
        function = new ToolArgumentRepairAIFunction(function, _maxInvalidCalls, rejectUnknownProperties: true);
        function = new BudgetedToolResultAIFunction(function, _maxResultCharacters);

        // Authoritative approval floor — unconditional, never read from a stored flag (C2). ApprovalRequiredAIFunction
        // stays the outermost type so the pipeline's approval detection and the unattended-run filter both see it.
        return new ApprovalRequiredAIFunction(function);
    }

    private static ToolCategory CategoryFor(CustomToolKind kind)
    {
        return kind == CustomToolKind.HttpFetch ? ToolCategory.Network : ToolCategory.WriteExecute;
    }
}
