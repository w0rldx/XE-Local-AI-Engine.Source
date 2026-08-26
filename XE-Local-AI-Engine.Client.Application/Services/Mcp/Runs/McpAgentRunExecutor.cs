namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Seeds the root spawn budget missing from a detached worker before invoking the execution boundary. The
///     whole-turn deadline is NOT applied here: it lives inside <c>SpawnForMcpAsync</c> so the synchronous
///     <c>run_agent</c> tool and this detached path are bounded by the node "Maximum message request timeout" exactly
///     once, on the same terms.
/// </summary>
internal sealed class McpAgentRunExecutor(
    IMcpAgentExecutionService executionService,
    IOptions<SpawnOptions> spawnOptions) : IMcpAgentRunExecutor
{
    private readonly IMcpAgentExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

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
                Instructions = run.Instructions,
                InboundContext = ToInboundContext(run),
                ExecutionRequestId = run.RequestId
            }
            : new McpExecutionBindingRequest
            {
                ModelId = run.ModelId,
                Instructions = run.Instructions,
                InboundContext = ToInboundContext(run),
                ExecutionRequestId = run.RequestId
            };

        using var root = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns);
        return await _executionService.SpawnForMcpAsync(bindingRequest,
            run.Task,
            Convert.ToHexString(run.BindingFingerprint.Value.Span),
            cancellationToken,
            run.WorkspaceId).ConfigureAwait(false);
    }

    private static McpInboundExecutionContext ToInboundContext(McpAgentRunRecord run)
    {
        if (!run.IsAgenticAutoApprove)
        {
            return McpInboundExecutionContext.Delegate;
        }

        if (!McpInboundExecutionContext.IsBoundedPrefix(run.RequestingKeyPrefix))
        {
            throw new InvalidDataException("The durable MCP run contains inconsistent captured agentic authority.");
        }

        return new McpInboundExecutionContext(McpServerApiKeyScope.Agentic, run.RequestingKeyPrefix);
    }
}
