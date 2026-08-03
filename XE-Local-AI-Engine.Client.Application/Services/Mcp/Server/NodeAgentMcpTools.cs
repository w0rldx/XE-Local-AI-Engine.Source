namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The tool surface this node exposes to EXTERNAL MCP clients (Claude Code, Claude Desktop, an IDE). The point of
///     the surface is delegation: an outside agent hands a task to a locally-hosted model instead of doing the work
///     itself, so private or bulky work never leaves the machine.
///     <para>
///         <b>Read-only by construction.</b> Every tool here reads node state or runs an agent; none writes. An MCP
///         caller is a strictly lesser principal than the browser operator and, crucially, has NO human-in-the-loop
///         route to answer a tool-approval request — the same reason <c>RunSavedAgentHandler</c> and
///         <c>SubAgentSpawnService</c> both strip approval-required tools from an unattended offer. Adding a writing
///         tool here is an operator decision that needs an approval story first, not an implementation detail.
///     </para>
/// </summary>
[McpServerToolType]
public sealed class NodeAgentMcpTools
{
    /// <summary>
    ///     Upper bound on the characters returned from a single agent run. Claude Code caps MCP tool output at ~25k
    ///     tokens by default and warns past ~10k, so an unbounded local-model answer would be truncated by the client
    ///     with no indication of why. Bounding here means the caller gets a clean, explicit marker instead.
    /// </summary>
    private const int MaxResultCharacters = 24_000;

    private const string TruncationMarker = "\n\n[output truncated by the XE Local AI Engine MCP server]";

    private readonly IAgentDefinitionStore _agentDefinitionStore;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ILogger<NodeAgentMcpTools> _logger;
    private readonly SpawnOptions _spawnOptions;
    private readonly ISubAgentSpawnService _spawnService;

    public NodeAgentMcpTools(ISubAgentSpawnService spawnService,
        IAgentDefinitionStore agentDefinitionStore,
        IGgufModelStore ggufModelStore,
        IOptions<SpawnOptions> spawnOptions,
        ILogger<NodeAgentMcpTools> logger)
    {
        _spawnService = spawnService ?? throw new ArgumentNullException(nameof(spawnService));
        _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        ArgumentNullException.ThrowIfNull(spawnOptions);
        _spawnOptions = spawnOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [McpServerTool(Name = "list_agents")]
    [Description("List the saved agents (personas) on this node that can be given a task with run_agent. Returns each agent's id, name and description.")]
    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        var definitions = await _agentDefinitionStore.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. definitions.Select(static definition => new AgentSummary(definition.Id.ToString(), definition.Name, definition.Description))];
    }

    [McpServerTool(Name = "list_models")]
    [Description("List the locally installed models on this node that run_agent can bind to directly when no saved agent is wanted.")]
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var models = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. models.Where(static model => model.IsAvailable)
                     .Select(static model => model.ModelName)
                     .OrderBy(static name => name, StringComparer.Ordinal)
        ];
    }

    [McpServerTool(Name = "run_agent")]
    [Description("Run a task on this node's local model and return the result. Supply either agent (a saved agent's id or name, which brings its persona, tools and skills) or model (a local model id) — exactly one. Runs are admission-gated: a request that would exceed the node's memory or concurrency limits is declined with a reason rather than queued indefinitely.")]
    // Parameter order is dictated by C#, not by preference: `progress` and `cancellationToken` are injected by the SDK
    // and excluded from the generated schema, but they carry no default, so they must precede the optional arguments.
    // Those defaults are load-bearing — the SDK derives `required` from the ABSENCE of a default, not from nullability,
    // so a nullable-but-defaultless `string? agent` is advertised as REQUIRED and every call that binds a bare model is
    // rejected by the binder before the handler runs (measured live: "the arguments dictionary is missing a value for
    // the required parameter 'agent'"). Do not remove the `= null`s.
    public async Task<string> RunAgentAsync(
        [Description("The task for the local agent to carry out.")] string task,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken,
        [Description("A saved agent's id or name. Mutually exclusive with model.")] string? agent = null,
        [Description("A local model id to bind an ad-hoc agent to. Mutually exclusive with agent.")] string? model = null,
        [Description("Optional system-prompt override. Only applies when binding a bare model; ignored when a saved agent is named.")] string? instructions = null)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return "Cannot run: provide a non-empty task.";
        }

        if (string.IsNullOrWhiteSpace(agent) == string.IsNullOrWhiteSpace(model))
        {
            return "Cannot run: provide exactly one of agent or model.";
        }

        // A local model can take well over a minute to load and generate. An MCP client aborts a call that produces
        // neither a response nor a progress notification inside its idle window (five minutes for Claude Code), so an
        // early progress report is what keeps a legitimate cold-start run alive rather than being killed as hung.
        progress.Report(new ProgressNotificationValue { Progress = 0f, Message = "Admitting the run on the local node…" });

        // The fan-out and cloud-spawn caps hang off a per-root-invocation SpawnContext, which a chat turn seeds and an
        // MCP call has no equivalent of. Seed one synthetic root per call so an MCP-driven run is bounded by exactly
        // the same caps as an operator-driven one instead of running uncapped.
        using var spawnRoot = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns);

        var request = new SubAgentSpawnRequest
        {
            SubAgentKey = string.IsNullOrWhiteSpace(agent) ? null : agent,
            ModelId = string.IsNullOrWhiteSpace(model) ? null : model,
            Task = task,
            Instructions = instructions
        };

        progress.Report(new ProgressNotificationValue { Progress = 0.1f, Message = "Running on the local model…" });

        // SubAgentSpawnService returns a sanitized reason string for every EXPECTED rejection (over-cap, no fit, busy,
        // unresolved agent/model) rather than throwing, so those reach the caller as an ordinary tool result. Only a
        // genuinely exceptional fault propagates, and the SDK turns that into a protocol error.
        var result = await _spawnService.SpawnAsync(request, cancellationToken).ConfigureAwait(false);

        progress.Report(new ProgressNotificationValue { Progress = 1f, Message = "Completed." });

        if (result.Length <= MaxResultCharacters)
        {
            return result;
        }

        _logger.LogInformation("An MCP run_agent result was truncated from {ActualLength} to {MaxLength} characters before returning it to the client.",
            result.Length,
            MaxResultCharacters);

        return string.Concat(result.AsSpan(0, MaxResultCharacters), TruncationMarker);
    }

    /// <summary>One saved agent, as offered to an external MCP client. Ids are stringified for a JSON-schema-friendly shape.</summary>
    public sealed record AgentSummary(string Id, string Name, string? Description);
}
