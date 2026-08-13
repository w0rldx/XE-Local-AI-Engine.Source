namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Emits one terminal, content-free efficiency record for an admitted production invocation. Metrics carry only
///     bounded dimensions; the trace and debug record carry correlation plus numeric aggregates, never prompt/model/tool
///     content. This is intentionally transient observability rather than a second persistence ledger.
/// </summary>
internal static class InvocationEfficiencyTelemetry
{
    internal static void Record(InvocationEfficiencyRecord record, Activity? activity, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(logger);

        var tags = new TagList
        {
            { "provider", record.Provider },
            { "outcome", record.Outcome },
            { "orchestration", record.Orchestration }
        };

        var efficiency = record.ProviderEfficiency;
        NodeMetrics.AgentHarnessInvocationTotal.Add(1, tags);
        NodeMetrics.AgentHarnessTotalDurationMs.Record(record.TotalDurationMs, tags);
        if (record.PreRunDurationMs is { } preRunDurationMs)
        {
            NodeMetrics.AgentHarnessPreRunDurationMs.Record(preRunDurationMs, tags);
        }

        if (record.QueueDurationMs is { } queueDurationMs)
        {
            NodeMetrics.AgentHarnessQueueDurationMs.Record(queueDurationMs, tags);
        }

        if (record.ModelReadinessDurationMs is { } modelReadinessMs)
        {
            NodeMetrics.AgentHarnessModelReadinessMs.Record(modelReadinessMs, tags);
        }

        if (record.FirstOutputLatencyMs is { } firstOutputMs)
        {
            NodeMetrics.AgentHarnessFirstOutputMs.Record(firstOutputMs, tags);
        }

        NodeMetrics.AgentHarnessProviderCalls.Record(efficiency.ProviderCalls, tags);
        NodeMetrics.AgentHarnessEstimatedInputTokens.Record(efficiency.EstimatedInputTokens, tags);
        if (record.InputTokens is { } inputTokens)
        {
            NodeMetrics.AgentHarnessReportedInputTokens.Record(inputTokens, tags);
        }

        if (record.OutputTokens is { } outputTokens)
        {
            NodeMetrics.AgentHarnessReportedOutputTokens.Record(outputTokens, tags);
        }

        NodeMetrics.AgentHarnessToolSchemaTokens.Record(efficiency.ToolSchemaTokens, tags);
        NodeMetrics.AgentHarnessProviderRoundElapsedMs.Record(efficiency.ProviderRoundElapsedMs, tags);
        NodeMetrics.AgentHarnessToolCalls.Record(efficiency.ToolCallsRequested, tags);
        NodeMetrics.AgentHarnessToolRequestToResultMs.Record(efficiency.ToolRequestToResultMs, tags);
        NodeMetrics.AgentHarnessToolResultBytes.Record(efficiency.ToolResultBytes, tags);
        NodeMetrics.AgentHarnessProviderRetries.Record(efficiency.ProviderRetries, tags);
        NodeMetrics.AgentHarnessToolArgumentRepairs.Record(efficiency.ToolArgumentRepairs, tags);
        NodeMetrics.AgentHarnessHandoffs.Record(efficiency.AgentHandoffs, tags);
        NodeMetrics.AgentHarnessMessagesDropped.Record(efficiency.MessagesDropped, tags);
        NodeMetrics.AgentHarnessToolResultsTruncated.Record(efficiency.ToolResultsTruncated, tags);
        if (efficiency.TimeToFirstToolRequestMs is { } firstToolRequestMs)
        {
            NodeMetrics.AgentHarnessFirstToolRequestMs.Record(firstToolRequestMs, tags);
        }

        TagActivity(activity, record);
        LogDebugRecord(logger, record);
    }

    private static void TagActivity(Activity? activity, InvocationEfficiencyRecord record)
    {
        if (activity is null)
        {
            return;
        }

        var efficiency = record.ProviderEfficiency;
        activity.SetTag("harness.outcome", record.Outcome);
        activity.SetTag("harness.provider", record.Provider);
        activity.SetTag("harness.orchestration", record.Orchestration);
        activity.SetTag("harness.total_duration_ms", record.TotalDurationMs);
        activity.SetTag("harness.pre_run_duration_ms", record.PreRunDurationMs);
        activity.SetTag("harness.queue_duration_ms", record.QueueDurationMs);
        activity.SetTag("harness.model_readiness_ms", record.ModelReadinessDurationMs);
        activity.SetTag("harness.first_output_ms", record.FirstOutputLatencyMs);
        activity.SetTag("harness.input_tokens", record.InputTokens);
        activity.SetTag("harness.output_tokens", record.OutputTokens);
        activity.SetTag("harness.reasoning_tokens", record.ReasoningTokens);
        activity.SetTag("harness.provider_calls", efficiency.ProviderCalls);
        activity.SetTag("harness.provider_rounds_rejected", efficiency.ProviderRoundsRejected);
        activity.SetTag("harness.estimated_input_tokens", efficiency.EstimatedInputTokens);
        activity.SetTag("harness.maximum_estimated_input_tokens", efficiency.MaximumEstimatedInputTokens);
        activity.SetTag("harness.tool_schema_tokens", efficiency.ToolSchemaTokens);
        activity.SetTag("harness.maximum_tool_schema_tokens", efficiency.MaximumToolSchemaTokens);
        activity.SetTag("harness.provider_round_elapsed_ms", efficiency.ProviderRoundElapsedMs);
        activity.SetTag("harness.messages_dropped", efficiency.MessagesDropped);
        activity.SetTag("harness.tool_results_truncated", efficiency.ToolResultsTruncated);
        activity.SetTag("harness.chars_truncated", efficiency.CharsTruncated);
        activity.SetTag("harness.tool_calls_requested", efficiency.ToolCallsRequested);
        activity.SetTag("harness.tool_calls_completed", efficiency.ToolCallsCompleted);
        activity.SetTag("harness.tool_calls_failed", efficiency.ToolCallsFailed);
        activity.SetTag("harness.tool_request_to_result_ms", efficiency.ToolRequestToResultMs);
        activity.SetTag("harness.tool_result_bytes", efficiency.ToolResultBytes);
        activity.SetTag("harness.first_tool_request_ms", efficiency.TimeToFirstToolRequestMs);
        activity.SetTag("harness.provider_retries", efficiency.ProviderRetries);
        activity.SetTag("harness.tool_argument_repairs", efficiency.ToolArgumentRepairs);
        activity.SetTag("harness.agent_handoffs", efficiency.AgentHandoffs);
    }

    private static void LogDebugRecord(ILogger logger, InvocationEfficiencyRecord record)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var efficiency = record.ProviderEfficiency;
        logger.LogDebug(
            "AgentHarnessEfficiency InvocationId={InvocationId} Outcome={Outcome} Provider={Provider} Orchestration={Orchestration} TotalDurationMs={TotalDurationMs} PreRunDurationMs={PreRunDurationMs} QueueDurationMs={QueueDurationMs} ModelReadinessMs={ModelReadinessMs} FirstOutputMs={FirstOutputMs} InputTokens={InputTokens} OutputTokens={OutputTokens} ReasoningTokens={ReasoningTokens} ProviderCalls={ProviderCalls} ProviderRoundsRejected={ProviderRoundsRejected} EstimatedInputTokens={EstimatedInputTokens} MaximumEstimatedInputTokens={MaximumEstimatedInputTokens} ToolSchemaTokens={ToolSchemaTokens} MaximumToolSchemaTokens={MaximumToolSchemaTokens} ProviderRoundElapsedMs={ProviderRoundElapsedMs} MessagesDropped={MessagesDropped} ToolResultsTruncated={ToolResultsTruncated} CharsTruncated={CharsTruncated} ToolCallsRequested={ToolCallsRequested} ToolCallsCompleted={ToolCallsCompleted} ToolCallsFailed={ToolCallsFailed} ToolRequestToResultMs={ToolRequestToResultMs} ToolResultBytes={ToolResultBytes} FirstToolRequestMs={FirstToolRequestMs} ProviderRetries={ProviderRetries} ToolArgumentRepairs={ToolArgumentRepairs} AgentHandoffs={AgentHandoffs}",
            record.InvocationId,
            record.Outcome,
            record.Provider,
            record.Orchestration,
            record.TotalDurationMs,
            record.PreRunDurationMs,
            record.QueueDurationMs,
            record.ModelReadinessDurationMs,
            record.FirstOutputLatencyMs,
            record.InputTokens,
            record.OutputTokens,
            record.ReasoningTokens,
            efficiency.ProviderCalls,
            efficiency.ProviderRoundsRejected,
            efficiency.EstimatedInputTokens,
            efficiency.MaximumEstimatedInputTokens,
            efficiency.ToolSchemaTokens,
            efficiency.MaximumToolSchemaTokens,
            efficiency.ProviderRoundElapsedMs,
            efficiency.MessagesDropped,
            efficiency.ToolResultsTruncated,
            efficiency.CharsTruncated,
            efficiency.ToolCallsRequested,
            efficiency.ToolCallsCompleted,
            efficiency.ToolCallsFailed,
            efficiency.ToolRequestToResultMs,
            efficiency.ToolResultBytes,
            efficiency.TimeToFirstToolRequestMs,
            efficiency.ProviderRetries,
            efficiency.ToolArgumentRepairs,
            efficiency.AgentHandoffs);
    }
}

/// <summary>
///     Terminal numeric view of one invocation. <see cref="Provider" /> is the bounded provider category used for
///     telemetry dimensions; <see cref="ProviderEfficiency" /> contains the content-free provider/tool aggregates.
/// </summary>
internal sealed record InvocationEfficiencyRecord(
    Guid InvocationId,
    string Outcome,
    string Provider,
    bool Orchestration,
    double TotalDurationMs,
    double? PreRunDurationMs,
    double? QueueDurationMs,
    double? ModelReadinessDurationMs,
    double? FirstOutputLatencyMs,
    int? InputTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    ProviderCallEfficiencySnapshot ProviderEfficiency);
