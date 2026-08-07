namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using NSubstitute;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class McpAgentRunMetricsTests
{
    private static readonly string[] ForbiddenTags =
    [
        "request_id",
        "agent_definition_id",
        "workspace_id",
        "model_id",
        "task",
        "failure_message"
    ];

    [Test]
    public void Update_ObservableGaugesReportExactCommittedSnapshotWithoutTags()
    {
        using var capture = new NodeMeterCapture();
        using var metrics = new McpAgentRunMetrics();
        var counters = new McpAgentRunLedgerCounters(AccountingVersion: 1,
            NonterminalRunCount: 7,
            QueuedRunCount: 5,
            RunningRunCount: 2,
            IdentityCount: 19,
            ActivePayloadBytes: 1234,
            TombstoneLogicalBytes: 5678,
            UpdatedAtUtc: 99);

        metrics.Update(new McpAgentRunLedgerSnapshot(QueueDepth: 5, RunningCount: 2, counters));
        capture.Observe();

        AssertGauge(capture, "mcp_agent_run_queue_depth", 5);
        AssertGauge(capture, "mcp_agent_run_running", 2);
        AssertGauge(capture, "mcp_agent_run_nonterminal", 7);
        AssertGauge(capture, "mcp_agent_run_identity_count", 19);
        AssertGauge(capture, "mcp_agent_run_active_payload_bytes", 1234);
        AssertGauge(capture, "mcp_agent_run_tombstone_bytes", 5678);
        AssertGauge(capture, "mcp_agent_run_accounting_version", 1);
        AssertEx.Equal(typeof(long), capture.MeasurementType("mcp_agent_run_queue_depth"));
        AssertEx.Equal(typeof(long), capture.MeasurementType("mcp_agent_run_running"));
        AssertEx.Equal(typeof(long), capture.MeasurementType("mcp_agent_run_nonterminal"));
        AssertEx.Equal(typeof(long), capture.MeasurementType("mcp_agent_run_identity_count"));
        AssertEx.Equal(typeof(long), capture.MeasurementType("mcp_agent_run_active_payload_bytes"));
        AssertEx.Equal(typeof(long), capture.MeasurementType("mcp_agent_run_tombstone_bytes"));
        AssertEx.Equal(typeof(long), capture.MeasurementType("mcp_agent_run_accounting_version"));
        AssertNoForbiddenTags(capture.AllMeasurements);
    }

    [Test]
    public void RecordLifecycleSignals_EmitExpectedValuesWithoutHighCardinalityTags()
    {
        using var capture = new NodeMeterCapture();
        using var metrics = new McpAgentRunMetrics();

        metrics.RecordLifecycle("request_id_conflict");
        metrics.RecordQuota("active_payload_bytes");
        metrics.RecordStop("watchdog", "requested");
        metrics.RecordRecovery("running_terminalized", count: 3);

        AssertMeasurement(capture, "mcp_agent_run_lifecycle_total", expectedValue: 1, "outcome", "request_id_conflict");
        AssertMeasurement(capture, "mcp_agent_run_quota_rejected_total", expectedValue: 1, "quota", "active_payload_bytes");
        AssertMeasurement(capture, "mcp_agent_run_recovery_total", expectedValue: 3, "outcome", "running_terminalized");
        var stop = capture.Measurements("mcp_agent_run_stop_total");
        AssertEx.Equal(expected: 1, stop.Count);
        AssertEx.Equal("watchdog", (string?)stop[0].Tag("reason"));
        AssertEx.Equal("requested", (string?)stop[0].Tag("outcome"));
        AssertEx.Equal(expected: 2, stop[0].Tags.Count);
        AssertNoForbiddenTags(capture.AllMeasurements);
    }

    [Test]
    public void RecordClaimAge_WhenClockWouldBeNegative_ClampsMeasurementToZero()
    {
        using var capture = new NodeMeterCapture();
        using var metrics = new McpAgentRunMetrics();

        metrics.RecordClaimAge(milliseconds: -25);

        var measurements = capture.Measurements("mcp_agent_run_claim_age_ms");
        AssertEx.Equal(expected: 1, measurements.Count);
        AssertEx.Equal(expected: 0d, measurements[0].Value);
        AssertNoForbiddenTags(measurements);
    }

    [Test]
    public async Task RefreshAsync_AfterCommittedLifecycleSnapshots_UpdatesEveryCurrentStateGauge()
    {
        using var capture = new NodeMeterCapture();
        using var metrics = new McpAgentRunMetrics();
        var store = Substitute.For<IMcpAgentRunStore>();
        var snapshots = new[]
        {
            Snapshot(queue: 1, running: 0, nonterminal: 1, identities: 1, activeBytes: 800, tombstoneBytes: 288), // start
            Snapshot(queue: 0, running: 1, nonterminal: 1, identities: 1, activeBytes: 800, tombstoneBytes: 288), // claim
            Snapshot(queue: 0, running: 0, nonterminal: 0, identities: 1, activeBytes: 640, tombstoneBytes: 288), // finalize
            Snapshot(queue: 0, running: 0, nonterminal: 0, identities: 1, activeBytes: 0, tombstoneBytes: 288), // compaction
            Snapshot(queue: 0, running: 0, nonterminal: 0, identities: 2, activeBytes: 400, tombstoneBytes: 576) // queued cancel
        };
        var index = 0;
        store.GetLedgerSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ => snapshots[index++]);

        foreach (var snapshot in snapshots)
        {
            await metrics.RefreshAsync(store, CancellationToken.None).ConfigureAwait(false);
            capture.Observe();
            AssertLatestGauge(capture, "mcp_agent_run_queue_depth", snapshot.QueueDepth);
            AssertLatestGauge(capture, "mcp_agent_run_running", snapshot.RunningCount);
            AssertLatestGauge(capture, "mcp_agent_run_nonterminal", snapshot.Counters.NonterminalRunCount);
            AssertLatestGauge(capture, "mcp_agent_run_identity_count", snapshot.Counters.IdentityCount);
            AssertLatestGauge(capture, "mcp_agent_run_active_payload_bytes", snapshot.Counters.ActivePayloadBytes);
            AssertLatestGauge(capture, "mcp_agent_run_tombstone_bytes", snapshot.Counters.TombstoneLogicalBytes);
            AssertLatestGauge(capture, "mcp_agent_run_accounting_version", snapshot.Counters.AccountingVersion);
        }

        await store.Received(snapshots.Length).GetLedgerSnapshotAsync(Arg.Any<CancellationToken>());
        AssertNoForbiddenTags(capture.AllMeasurements);
    }

    [Test]
    public async Task RefreshAsync_WhenOlderReadIsBlocked_SerializesReadAndPublishSoNewerSnapshotWins()
    {
        using var capture = new NodeMeterCapture();
        using var metrics = new McpAgentRunMetrics();
        var store = Substitute.For<IMcpAgentRunStore>();
        var older = Snapshot(queue: 1, running: 0, nonterminal: 1, identities: 1, activeBytes: 800, tombstoneBytes: 288,
            accountingVersion: 1);
        var newer = Snapshot(queue: 2, running: 1, nonterminal: 3, identities: 4, activeBytes: 1600, tombstoneBytes: 576,
            accountingVersion: 2);
        var firstReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        store.GetLedgerSnapshotAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstReadStarted.TrySetResult();
                await releaseFirstRead.Task.ConfigureAwait(false);
                return older;
            }

            secondReadStarted.TrySetResult();
            return newer;
        });

        var firstRefresh = metrics.RefreshAsync(store, CancellationToken.None);
        await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var secondRefresh = metrics.RefreshAsync(store, CancellationToken.None);
        var secondReadOverlappedFirst = secondReadStarted.Task.IsCompleted;
        releaseFirstRead.TrySetResult();
        await Task.WhenAll(firstRefresh, secondRefresh).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        AssertEx.False(secondReadOverlappedFirst, "The second committed-state read must wait until the first read and publication complete.");
        AssertEx.True(secondReadStarted.Task.IsCompleted);
        AssertEx.Equal(expected: 2, callCount);
        capture.Observe();
        AssertLatestGauge(capture, "mcp_agent_run_queue_depth", newer.QueueDepth);
        AssertLatestGauge(capture, "mcp_agent_run_running", newer.RunningCount);
        AssertLatestGauge(capture, "mcp_agent_run_nonterminal", newer.Counters.NonterminalRunCount);
        AssertLatestGauge(capture, "mcp_agent_run_identity_count", newer.Counters.IdentityCount);
        AssertLatestGauge(capture, "mcp_agent_run_active_payload_bytes", newer.Counters.ActivePayloadBytes);
        AssertLatestGauge(capture, "mcp_agent_run_tombstone_bytes", newer.Counters.TombstoneLogicalBytes);
        AssertLatestGauge(capture, "mcp_agent_run_accounting_version", newer.Counters.AccountingVersion);
    }

    private static void AssertMeasurement(NodeMeterCapture capture,
        string instrumentName,
        double expectedValue,
        string tagName,
        string tagValue)
    {
        var measurements = capture.Measurements(instrumentName);
        AssertEx.Equal(expected: 1, measurements.Count);
        AssertEx.Equal(expectedValue, measurements[0].Value);
        AssertEx.Equal(tagValue, (string?)measurements[0].Tag(tagName));
        AssertEx.Equal(expected: 1, measurements[0].Tags.Count);
    }

    private static void AssertGauge(NodeMeterCapture capture, string instrumentName, double expectedValue)
    {
        var measurements = capture.Measurements(instrumentName);
        AssertEx.Equal(expected: 1, measurements.Count);
        AssertEx.Equal(expectedValue, measurements[0].Value);
        AssertEx.Empty(measurements[0].Tags);
    }

    private static void AssertLatestGauge(NodeMeterCapture capture, string instrumentName, double expectedValue)
    {
        var measurements = capture.Measurements(instrumentName);
        var measurement = measurements[measurements.Count - 1];
        AssertEx.Equal(expectedValue, measurement.Value);
        AssertEx.Empty(measurement.Tags);
    }

    private static McpAgentRunLedgerSnapshot Snapshot(long queue,
        long running,
        long nonterminal,
        long identities,
        long activeBytes,
        long tombstoneBytes,
        int accountingVersion = 1) =>
        new(queue,
            running,
            new McpAgentRunLedgerCounters(accountingVersion,
                NonterminalRunCount: nonterminal,
                QueuedRunCount: queue,
                RunningRunCount: running,
                IdentityCount: identities,
                ActivePayloadBytes: activeBytes,
                TombstoneLogicalBytes: tombstoneBytes,
                UpdatedAtUtc: 1));

    private static void AssertNoForbiddenTags(IEnumerable<CapturedMeasurement> measurements)
    {
        foreach (var measurement in measurements)
        {
            foreach (var forbiddenTag in ForbiddenTags)
            {
                AssertEx.False(measurement.Tags.ContainsKey(forbiddenTag),
                    $"Durable MCP telemetry must not expose high-cardinality/content tag '{forbiddenTag}'.");
            }
        }
    }

    private sealed record CapturedMeasurement(double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public object? Tag(string name) => Tags.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class NodeMeterCapture : IDisposable
    {
        private readonly ConcurrentQueue<(string Name, CapturedMeasurement Measurement)> _measurements = [];
        private readonly ConcurrentDictionary<string, Type> _measurementTypes = new(StringComparer.Ordinal);
        private readonly MeterListener _listener = new();

        public NodeMeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, NodeMetrics.MeterName, StringComparison.Ordinal)
                    && instrument.Name.StartsWith("mcp_agent_run_", StringComparison.Ordinal))
                {
                    _measurementTypes[instrument.Name] = instrument.GetType().GenericTypeArguments.Single();
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _measurements.Enqueue((instrument.Name, new CapturedMeasurement(value, ToDictionary(tags)))));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                _measurements.Enqueue((instrument.Name, new CapturedMeasurement(value, ToDictionary(tags)))));
            _listener.Start();
        }

        public IReadOnlyList<CapturedMeasurement> AllMeasurements => _measurements.Select(entry => entry.Measurement).ToArray();

        public IReadOnlyList<CapturedMeasurement> Measurements(string instrumentName) =>
            _measurements.Where(entry => string.Equals(entry.Name, instrumentName, StringComparison.Ordinal))
                         .Select(entry => entry.Measurement)
                         .ToArray();

        public Type MeasurementType(string instrumentName) => _measurementTypes[instrumentName];

        public void Observe() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();

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
