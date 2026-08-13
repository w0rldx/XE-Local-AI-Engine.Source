namespace XE_Local_AI_Engine.Tests.Telemetry;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class InvocationEfficiencyTelemetryTests
{
    [Test]
    public void Record_EmitsBoundedMetricsAndNumericActivityTags()
    {
        using var capture = new NodeMeterCapture();
        using var activity = new Activity("invocation-test");
        activity.Start();
        var efficiency = new ProviderCallEfficiencySnapshot(ProviderCalls: 3,
            ProviderRoundsRejected: 1,
            EstimatedInputTokens: 2400,
            MaximumEstimatedInputTokens: 900,
            ToolSchemaTokens: 600,
            MaximumToolSchemaTokens: 200,
            ProviderRoundElapsedMs: 1250,
            MessagesDropped: 2,
            ToolResultsTruncated: 1,
            CharsTruncated: 4000,
            ToolCallsRequested: 2,
            ToolCallsCompleted: 2,
            ToolCallsFailed: 1,
            ToolRequestToResultMs: 75,
            ToolResultBytes: 8192,
            TimeToFirstToolRequestMs: 320,
            ProviderRetries: 1,
            ToolArgumentRepairs: 1,
            AgentHandoffs: 2);
        var record = new InvocationEfficiencyRecord(Guid.NewGuid(),
            "completed",
            "local",
            Orchestration: true,
            TotalDurationMs: 1800,
            PreRunDurationMs: 100,
            QueueDurationMs: 60,
            ModelReadinessDurationMs: 250,
            FirstOutputLatencyMs: 40,
            InputTokens: 800,
            OutputTokens: 120,
            ReasoningTokens: 30,
            ProviderEfficiency: efficiency);

        InvocationEfficiencyTelemetry.Record(record, activity, NullLogger.Instance);

        var terminal = capture.Longs("agent_harness_invocation_total").Single();
        AssertEx.Equal(expected: 1L, terminal.Value);
        AssertEx.Equal("local", terminal.Tag("provider") as string);
        AssertEx.Equal("completed", terminal.Tag("outcome") as string);
        AssertEx.True(terminal.Tag("orchestration") is true);
        AssertEx.Equal(expected: 3L, capture.Longs("agent_harness_provider_calls").Single().Value);
        AssertEx.Equal(expected: 2400L, capture.Longs("agent_harness_estimated_input_tokens").Single().Value);
        AssertEx.Equal(expected: 800L, capture.Longs("agent_harness_reported_input_tokens").Single().Value);
        AssertEx.Equal(expected: 120L, capture.Longs("agent_harness_reported_output_tokens").Single().Value);
        AssertEx.Equal(expected: 600L, capture.Longs("agent_harness_tool_schema_tokens").Single().Value);
        AssertEx.Equal(expected: 2L, capture.Longs("agent_harness_tool_calls").Single().Value);
        AssertEx.Equal(expected: 1L, capture.Longs("agent_harness_provider_retries").Single().Value);
        AssertEx.Equal(expected: 1L, capture.Longs("agent_harness_tool_argument_repairs").Single().Value);
        AssertEx.Equal(expected: 2L, capture.Longs("agent_harness_handoffs").Single().Value);
        AssertEx.Equal(expected: 1800d, capture.Doubles("agent_harness_total_duration_ms").Single().Value);
        AssertEx.Equal(expected: 100d, capture.Doubles("agent_harness_pre_run_duration_ms").Single().Value);
        AssertEx.Equal(expected: 60d, capture.Doubles("agent_harness_queue_duration_ms").Single().Value);
        AssertEx.Equal(expected: 250d, capture.Doubles("agent_harness_model_readiness_ms").Single().Value);
        AssertEx.Equal(expected: 40d, capture.Doubles("agent_harness_first_output_ms").Single().Value);
        AssertEx.Equal(expected: 1250d, capture.Doubles("agent_harness_provider_round_elapsed_ms").Single().Value);
        AssertEx.Equal(expected: 75d, capture.Doubles("agent_harness_tool_request_to_result_ms").Single().Value);
        AssertEx.Equal(expected: 320d, capture.Doubles("agent_harness_first_tool_request_ms").Single().Value);
        AssertEx.Equal("completed", activity.GetTagItem("harness.outcome") as string);
        AssertEx.Equal(expected: 3, Convert.ToInt32(activity.GetTagItem("harness.provider_calls")));
        AssertEx.Equal(expected: 600L, Convert.ToInt64(activity.GetTagItem("harness.tool_schema_tokens")));
    }

    private readonly record struct CapturedLong(long Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public object? Tag(string key)
        {
            return Tags.TryGetValue(key, out var value) ? value : null;
        }
    }

    private readonly record struct CapturedDouble(double Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed class NodeMeterCapture : IDisposable
    {
        private readonly ConcurrentBag<(string Name, CapturedDouble Measurement)> _doubles = [];
        private readonly ConcurrentBag<(string Name, CapturedLong Measurement)> _longs = [];
        private readonly MeterListener _listener = new();

        public NodeMeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, NodeMetrics.MeterName, StringComparison.Ordinal)
                    && instrument.Name.StartsWith("agent_harness_", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _longs.Add((instrument.Name, new CapturedLong(value, ToDictionary(tags)))));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                _doubles.Add((instrument.Name, new CapturedDouble(value, ToDictionary(tags)))));
            _listener.Start();
        }

        public IReadOnlyList<CapturedLong> Longs(string instrumentName)
        {
            return _longs.Where(entry => string.Equals(entry.Name, instrumentName, StringComparison.Ordinal))
                         .Select(static entry => entry.Measurement)
                         .ToArray();
        }

        public IReadOnlyList<CapturedDouble> Doubles(string instrumentName)
        {
            return _doubles.Where(entry => string.Equals(entry.Name, instrumentName, StringComparison.Ordinal))
                           .Select(static entry => entry.Measurement)
                           .ToArray();
        }

        public void Dispose()
        {
            _listener.Dispose();
        }

        private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                dictionary[tag.Key] = tag.Value;
            }

            return dictionary;
        }
    }
}
