namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Seeds the root spawn budget missing from a detached worker before invoking the G001 execution boundary, and
///     bounds the whole turn by the operator's node-level "Maximum message request timeout" — the SAME knob that bounds
///     a local chat send/regenerate and a scheduled run. Without it an inbound MCP run had no whole-turn deadline at
///     all: only the dispatcher's coarse watchdog and the transport's own timeout applied. The setting is read per
///     execution, so a Save takes effect on the next run without a node restart.
/// </summary>
internal sealed class McpAgentRunExecutor(
    IMcpAgentExecutionService executionService,
    INodeSettingsStore nodeSettingsStore,
    IOptions<SpawnOptions> spawnOptions) : IMcpAgentRunExecutor
{
    private readonly IMcpAgentExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    private readonly INodeSettingsStore _nodeSettingsStore =
        nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));

    private readonly SpawnOptions _spawnOptions =
        (spawnOptions ?? throw new ArgumentNullException(nameof(spawnOptions))).Value;

    public async Task<SpawnOutcome> ExecuteAsync(McpAgentRunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (string.IsNullOrWhiteSpace(run.ModelId)
            || run.BindingFingerprint is not { Length: 32 }
            || string.IsNullOrWhiteSpace(run.Task))
        {
            return SpawnOutcome.Failed(McpExecutionFailureCodes.AgentConfigChanged,
                "Cannot run: the accepted execution payload is unavailable or invalid.");
        }

        var bindingRequest = run.AgentDefinitionId is { } agentDefinitionId
            ? new McpExecutionBindingRequest
            {
                AgentKey = agentDefinitionId.ToString("D"),
                ModelOverrideId = run.ModelOverrideId,
                Instructions = run.Instructions
            }
            : new McpExecutionBindingRequest
            {
                ModelId = run.ModelId,
                Instructions = run.Instructions
            };

        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Linked, not replaced: the dispatcher's execution token still wins (user cancel, watchdog, host shutdown) and
        // its durable stop marker still chooses the terminal outcome. This only adds the missing whole-turn deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(nodeSettings.MaxMessageRequestTimeoutSeconds));

        using var root = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns);
        try
        {
            return await _executionService.SpawnForMcpAsync(bindingRequest,
                run.Task,
                Convert.ToHexString(run.BindingFingerprint.Value.Span),
                deadline.Token,
                run.WorkspaceId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only OUR deadline fired. Returning a typed outcome instead of letting the cancellation escape to the
            // dispatcher's generic "ended before producing a result" makes get_agent_run report a distinguishable
            // failure_code the caller can act on.
            return SpawnOutcome.Failed(McpExecutionFailureCodes.TimedOut,
                "The run exceeded the node's maximum message request timeout.");
        }
    }
}
