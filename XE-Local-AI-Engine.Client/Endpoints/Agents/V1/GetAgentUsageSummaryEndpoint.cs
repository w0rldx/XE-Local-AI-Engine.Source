namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Read-only token-usage summary over the durable run-envelope ledger (<c>agent_execution_logs</c>, kind 1).
///     Operator-gated.
/// </summary>
/// <remarks>
///     Sums prompt/completion/reasoning/total tokens grouped by model name, fine-grained provider and UTC day over an
///     optional half-open date range, newest day first, plus a per-provider rollup, and attaches a server-computed USD
///     cost estimate per bucket/provider/total via <see cref="IUsageRateResolver" /> (local runtimes and unpriced
///     models are 0; reasoning bills as output). Metadata ONLY — token counts and a derived cost, nothing to redact —
///     over the retained horizon, whose window the response surfaces.
/// </remarks>
public sealed class GetAgentUsageSummaryEndpoint : Endpoint<AgentUsageSummaryRequest, AgentUsageSummaryResponse>
{
    private readonly AgentExecutionLogQueryService _executionLogs;
    private readonly IUsageRateResolver _rateResolver;
    private readonly IOptions<AgentExecutionLogRetentionOptions> _retentionOptions;

    public GetAgentUsageSummaryEndpoint(
        AgentExecutionLogQueryService executionLogs,
        IUsageRateResolver rateResolver,
        IOptions<AgentExecutionLogRetentionOptions> retentionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionLogs);
        ArgumentNullException.ThrowIfNull(rateResolver);
        ArgumentNullException.ThrowIfNull(retentionOptions);
        _executionLogs = executionLogs;
        _rateResolver = rateResolver;
        _retentionOptions = retentionOptions;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.UsageSummary);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(AgentUsageSummaryRequest req, CancellationToken ct)
    {
        var buckets = await _executionLogs.SummarizeTokenUsageAsync(req.FromEpochMs, req.ToEpochMs, ct);

        await Send.OkAsync(new AgentUsageSummaryResponse
            {
                Items = [.. buckets.Select(bucket => bucket.ToResponse(_rateResolver))],
                Totals = buckets.ToTotals(_rateResolver),
                ByProvider = buckets.ToByProvider(_rateResolver),
                RetentionDays = _retentionOptions.Value.RetentionDays
            },
            ct);
    }
}
