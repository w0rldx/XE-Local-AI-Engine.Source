namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Seeds the root spawn budget missing from a detached worker before invoking the execution boundary.
/// </summary>
/// <remarks>
///     The whole-turn deadline is NOT applied here: it lives inside <c>SpawnForMcpAsync</c>, so the synchronous
///     <c>run_agent</c> tool and this detached path are bounded by the node "Maximum message request timeout" exactly
///     once, on the same terms.
/// </remarks>
internal sealed class McpAgentRunExecutor : IMcpAgentRunExecutor
{
    private readonly IMcpAgentExecutionService _executionService;
    private readonly INodeRuntimeSettings _runtimeSettings;

    public McpAgentRunExecutor(IMcpAgentExecutionService executionService,
        INodeRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(executionService);
        _executionService = executionService;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    }

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

        using var root = SpawnContext.BeginRoot(await _runtimeSettings.GetSpawnMaxConcurrentAsync(cancellationToken),
            await _runtimeSettings.GetSpawnMaxCloudAsync(cancellationToken));
        return await _executionService.SpawnForMcpAsync(bindingRequest,
            run.Task,
            Convert.ToHexString(run.BindingFingerprint.Value.Span),
            cancellationToken,
            run.WorkspaceId);
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

        return new McpInboundExecutionContext
        {
            Scope = McpServerApiKeyScope.Agentic,
            KeyPrefix = run.RequestingKeyPrefix
        };
    }
}
