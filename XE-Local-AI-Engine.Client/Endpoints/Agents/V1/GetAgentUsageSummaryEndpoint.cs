namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Memory;

/// <summary>
///     Read-only token-usage summary over the durable run-envelope ledger (<c>agent_execution_logs</c>, kind 1). Sums
///     prompt/completion/reasoning/total tokens grouped by model name and UTC day over an optional half-open date range,
///     newest day first. Metadata ONLY — token counts, no message content, so nothing to redact. The table has no
///     provider column, so usage is grouped by model id only (the response flags this). Does NOT compute currency cost —
///     there is no price table; tokens only (a cost table is a follow-up). Only covers the retained horizon (the response
///     surfaces the retention window). Operator-gated.
/// </summary>
public sealed class GetAgentUsageSummaryEndpoint(
    IAgentExecutionLogStore executionLogStore,
    IOptions<AgentExecutionLogRetentionOptions> retentionOptions)
    : Endpoint<AgentUsageSummaryRequest, AgentUsageSummaryResponse>
{
    private readonly IAgentExecutionLogStore _executionLogStore = executionLogStore ?? throw new ArgumentNullException(nameof(executionLogStore));
    private readonly IOptions<AgentExecutionLogRetentionOptions> _retentionOptions = retentionOptions ?? throw new ArgumentNullException(nameof(retentionOptions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.UsageSummary);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(AgentUsageSummaryRequest req, CancellationToken ct)
    {
        var buckets = await _executionLogStore.SummarizeTokenUsageAsync(req.FromEpochMs, req.ToEpochMs, ct).ConfigureAwait(false);

        await Send.OkAsync(new AgentUsageSummaryResponse
            {
                Items = [.. buckets.Select(static bucket => bucket.ToResponse())],
                Totals = buckets.ToTotals(),
                GroupedByModelOnly = true,
                RetentionDays = _retentionOptions.Value.RetentionDays
            },
            ct).ConfigureAwait(false);
    }
}
