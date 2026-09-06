namespace XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;

using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Live-reading implementation of <see cref="ICustomToolCatalog" />. Reads the enabled, acknowledged custom tools
///     from the node store on every call — once per call, however many names one resolution requests — and builds each
///     one's executable through the SAME wrapper stack the built-in and MCP tools use — arg-repair (strict, the schema
///     fully enumerates inputs) under a result budget under a forced
///     <see cref="ApprovalRequiredAIFunction" />. The approval wrap is unconditional: it does not read a stored flag, so
///     no per-agent override can strip it and the sub-agent/scheduler filter always sees a gated tool.
///     <para>
///         This is a SINGLETON (the invocation stack that consumes it is singleton), so the scoped, DbContext-backed
///         <see cref="ICustomToolStore" /> is read through a fresh scope per call rather than captured — the established
///         singleton→scoped-store pattern. The executor the built executable captures is itself a singleton, so a resolved
///         tool executes safely after the read scope is disposed.
///     </para>
/// </summary>
internal sealed class CustomToolCatalog : ICustomToolCatalog
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IReadOnlyDictionary<CustomToolKind, ICustomToolExecutor> _executors;
    private readonly int _maxResultCharacters;
    private readonly int _maxInvalidCalls;
    private readonly ILogger<CustomToolCatalog> _logger;

    public CustomToolCatalog(IServiceScopeFactory scopeFactory,
        INodeRuntimeSettings runtimeSettings,
        IEnumerable<ICustomToolExecutor> executors,
        IOptions<AgentToolPipelineOptions> pipelineOptions,
        ILogger<CustomToolCatalog> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(pipelineOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _executors = executors.ToDictionary(static executor => executor.Kind);
        _maxResultCharacters = pipelineOptions.Value.MaxToolResultCharacters;
        _maxInvalidCalls = pipelineOptions.Value.MaxConsecutiveInvalidToolCallsPerTool;
    }

    // Reads the whole custom-tool library through a fresh scope (this catalog is a singleton, the store is scoped). The
    // records are plain decrypted data, so they safely out-live the scope; the executable built from them captures only
    // singleton executors.
    private async Task<IReadOnlyList<CustomToolRecord>> ListToolsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICustomToolStore>();
        return await store.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await ListToolsAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        // Nothing requested: the caller already skips the call when an offer carries no custom name, so this is belt and
        // braces — and it must cost neither a settings read nor a store read.
        if (names.Count == 0)
        {
            return ReadOnlyDictionary<string, AITool>.Empty;
        }

        // Belt-and-suspenders node kill-switch. When custom tools are disabled at the node level, refuse to resolve one
        // even if a stale offer somehow reached the resolver. The offer merge already withholds custom tools when off, so
        // this is the second, execution-time gate. ONE check for the whole resolution instead of one per name.
        if (!await _runtimeSettings.GetCustomToolsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadOnlyDictionary<string, AITool>.Empty;
        }

        var requested = new HashSet<string>(names, StringComparer.Ordinal);

        // THE one read for the whole resolution operation, whatever the offer's custom-name count. Still a live read per
        // operation — the batch bounds the reads inside one resolution and is not a cache across resolutions.
        var tools = await ListToolsAsync(cancellationToken).ConfigureAwait(false);

        var resolved = new Dictionary<string, AITool>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            // claimed.Add carries the old FirstOrDefault(IsOfferable && name ==) semantic: the first offerable record for
            // a name owns it in store order, so a later duplicate is never tried even when this one then fails its
            // executor or schema check.
            if (!requested.Contains(tool.Name) || !IsOfferable(tool) || !claimed.Add(tool.Name))
            {
                continue;
            }

            if (!_executors.TryGetValue(tool.Kind, out var executor))
            {
                _logger.LogWarning("Custom tool {ToolName} has no executor for kind {Kind}.", tool.Name, tool.Kind);
                continue;
            }

            if (TryBuildSchema(tool, out var schema))
            {
                resolved[tool.Name] = BuildExecutable(tool, schema, executor);
            }
        }

        return resolved;
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
            CategoryFor(tool.Kind),
            tool.Mode == CustomToolMode.Fixed);
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

        // Authoritative approval floor — unconditional and never read from a stored flag. ApprovalRequiredAIFunction
        // stays the outermost type so the pipeline's approval detection and the unattended-run filter both see it.
        return new ApprovalRequiredAIFunction(function);
    }

    private static ToolCategory CategoryFor(CustomToolKind kind)
    {
        return kind == CustomToolKind.HttpFetch ? ToolCategory.Network : ToolCategory.WriteExecute;
    }
}
