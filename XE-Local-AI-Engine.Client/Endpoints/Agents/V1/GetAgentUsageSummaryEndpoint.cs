namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Read-only token-usage summary over the durable run-envelope ledger (<c>agent_execution_logs</c>, kind 1). Sums
///     prompt/completion/reasoning/total tokens grouped by model name, fine-grained provider, and UTC day over an optional
///     half-open date range, newest day first, plus a per-provider rollup. Also attaches a server-computed USD cost
///     estimate per bucket/provider/total via <see cref="IUsageRateResolver" /> (local runtimes and unpriced models are 0;
///     reasoning bills as output). Metadata ONLY — token counts + a derived cost, no message content, so nothing to
///     redact. Only covers the retained horizon (the response surfaces the retention window). Operator-gated.
/// </summary>
public sealed class GetAgentUsageSummaryEndpoint(
    IAgentExecutionLogStore executionLogStore,
    IUsageRateResolver rateResolver,
    IOptions<AgentExecutionLogRetentionOptions> retentionOptions)
    : Endpoint<AgentUsageSummaryRequest, AgentUsageSummaryResponse>
{
    private readonly IAgentExecutionLogStore _executionLogStore = executionLogStore ?? throw new ArgumentNullException(nameof(executionLogStore));
    private readonly IUsageRateResolver _rateResolver = rateResolver ?? throw new ArgumentNullException(nameof(rateResolver));
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
                Items = [.. buckets.Select(bucket => bucket.ToResponse(_rateResolver))],
                Totals = buckets.ToTotals(_rateResolver),
                ByProvider = buckets.ToByProvider(_rateResolver),
                RetentionDays = _retentionOptions.Value.RetentionDays
            },
            ct).ConfigureAwait(false);
    }
}
