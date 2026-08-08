namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using System.Diagnostics.Metrics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Content-free telemetry for durable MCP runs. Observable gauges read only atomically refreshed committed state;
///     their callbacks never query SQLite or derive counters.
/// </summary>
internal sealed class McpAgentRunMetrics : IDisposable
{
    private static readonly McpAgentRunLedgerSnapshot EmptySnapshot = new(QueueDepth: 0,
        RunningCount: 0,
        new McpAgentRunLedgerCounters(AccountingVersion: 0,
            NonterminalRunCount: 0,
            QueuedRunCount: 0,
            RunningRunCount: 0,
            IdentityCount: 0,
            ActivePayloadBytes: 0,
            TombstoneLogicalBytes: 0,
            UpdatedAtUtc: 0));

    private readonly Histogram<double> _claimAge;
    private readonly Counter<long> _lifecycle;
    private readonly ILogger<McpAgentRunMetrics>? _logger;
    private readonly Meter _meter = new(NodeMetrics.MeterName);
    private readonly Counter<long> _quotaRejected;
    private readonly Counter<long> _recovery;
    private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
    private readonly Counter<long> _stop;

    private McpAgentRunLedgerSnapshot _snapshot = EmptySnapshot;

    public McpAgentRunMetrics(ILogger<McpAgentRunMetrics>? logger = null)
    {
        _logger = logger;
        _lifecycle = _meter.CreateCounter<long>("mcp_agent_run_lifecycle_total",
            description: "Durable inbound MCP agent run lifecycle events by bounded outcome.");
        _quotaRejected = _meter.CreateCounter<long>("mcp_agent_run_quota_rejected_total",
            description: "Durable inbound MCP agent run admission rejections by bounded quota.");
        _stop = _meter.CreateCounter<long>("mcp_agent_run_stop_total",
            description: "Durable inbound MCP agent run stop events by bounded reason and outcome.");
        _recovery = _meter.CreateCounter<long>("mcp_agent_run_recovery_total",
            description: "Durable inbound MCP agent run startup recovery events by bounded outcome.");
        _claimAge = _meter.CreateHistogram<double>("mcp_agent_run_claim_age_ms",
            unit: "ms",
            description: "Elapsed time from durable acceptance until a worker claim.");

        _meter.CreateObservableGauge("mcp_agent_run_queue_depth", () => Volatile.Read(ref _snapshot).QueueDepth,
            description: "Current committed durable MCP run queue depth.");
        _meter.CreateObservableGauge("mcp_agent_run_running", () => Volatile.Read(ref _snapshot).RunningCount,
            description: "Current committed durable MCP running count.");
        _meter.CreateObservableGauge("mcp_agent_run_nonterminal", () => Volatile.Read(ref _snapshot).Counters.NonterminalRunCount,
            description: "Current committed durable MCP nonterminal count.");
        _meter.CreateObservableGauge("mcp_agent_run_identity_count", () => Volatile.Read(ref _snapshot).Counters.IdentityCount,
            description: "Current committed durable MCP retained request identity count.");
        _meter.CreateObservableGauge("mcp_agent_run_active_payload_bytes", () => Volatile.Read(ref _snapshot).Counters.ActivePayloadBytes,
            unit: "By",
            description: "Current committed durable MCP active encrypted-payload logical bytes.");
        _meter.CreateObservableGauge("mcp_agent_run_tombstone_bytes", () => Volatile.Read(ref _snapshot).Counters.TombstoneLogicalBytes,
            unit: "By",
            description: "Current committed durable MCP tombstone logical bytes.");
        _meter.CreateObservableGauge("mcp_agent_run_accounting_version", () => (long)Volatile.Read(ref _snapshot).Counters.AccountingVersion,
            description: "Current durable MCP ledger accounting version.");
    }

    public void Update(McpAgentRunLedgerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }

    public async Task RefreshAsync(IMcpAgentRunStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Update(await store.GetLedgerSnapshotAsync(cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                _ = _refreshGate.Release();
            }
        }
        catch (Exception exception)
        {
            NodeSqliteContention.Record("raw", exception, _logger);
            _logger?.LogWarning(exception, "Could not refresh durable MCP run gauges from committed state.");
        }
    }

    public void RecordLifecycle(string outcome) =>
        _lifecycle.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordQuota(string quota) =>
        _quotaRejected.Add(1, new KeyValuePair<string, object?>("quota", quota));

    public void RecordStop(string reason, string outcome) =>
        _stop.Add(1,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordRecovery(string outcome, long count = 1) =>
        _recovery.Add(count, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordClaimAge(long milliseconds) =>
        _claimAge.Record(Math.Max(0, milliseconds));

    public void Dispose()
    {
        _refreshGate.Dispose();
        _meter.Dispose();
    }
}
