namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Read-only adaptive-memory execution-log diagnostics for one agent. Returns a page of metadata-only telemetry
///     rows (latency/tokens/success/errorClass/configHash/link ids) newest-first — there is NO message content in this
///     store, so nothing to redact; <c>ErrorClass</c> is an exception type name only by the store contract. Resolves the
///     agent first so a missing definition returns 404 rather than an empty page. Operator-gated.
/// </summary>
public sealed class ListAgentExecutionLogsEndpoint(
    IAgentDefinitionStore agentDefinitionStore,
    IAgentExecutionLogStore executionLogStore)
    : Endpoint<ListAgentExecutionLogsRequest, ListAgentExecutionLogsResponse>
{
    // Default page size when the caller supplies none; clamped upper bound keeps a diagnostics fetch bounded.
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly IAgentExecutionLogStore _executionLogStore = executionLogStore ?? throw new ArgumentNullException(nameof(executionLogStore));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.ExecutionLogs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListAgentExecutionLogsRequest req, CancellationToken ct)
    {
        var agent = await _agentDefinitionStore.GetByIdAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        var limit = req.Limit is { } requested && requested > 0 ? Math.Min(requested, MaxPageSize) : DefaultPageSize;
        var offset = req.Offset is { } requestedOffset && requestedOffset > 0 ? requestedOffset : 0;

        var records = await _executionLogStore.ListByAgentAsync(req.AgentDefinitionId, limit, offset, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListAgentExecutionLogsResponse
            {
                Items = [.. records.Select(static record => record.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
