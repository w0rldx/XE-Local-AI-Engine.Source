namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>Seeds the root spawn budget missing from a detached worker before invoking the G001 execution boundary.</summary>
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
                Instructions = run.Instructions
            }
            : new McpExecutionBindingRequest
            {
                ModelId = run.ModelId,
                Instructions = run.Instructions
            };

        using var root = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns);
        return await _executionService.SpawnForMcpAsync(bindingRequest,
            run.Task,
            Convert.ToHexString(run.BindingFingerprint.Value.Span),
            cancellationToken,
            run.WorkspaceId).ConfigureAwait(false);
    }
}
