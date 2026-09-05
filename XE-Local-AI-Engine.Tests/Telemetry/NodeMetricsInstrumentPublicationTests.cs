namespace XE_Local_AI_Engine.Tests.Telemetry;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

// Asserts the observability instruments are published on the shared "XE.Node" meter and carry their documented,
// bounded tags. A wrong meter name or a dropped tag would silently strip the signal from the exporter; capturing through
// a real MeterListener (the same surface OpenTelemetry attaches) catches that. Serial so a sibling test's node-meter
// emission cannot bleed into a capture window.
[NotInParallel]
public sealed class NodeMetricsInstrumentPublicationTests
{
    [Test]
    public void TurnToModelLoadStartMs_RecordsOnNodeMeter_WithProviderTag()
    {
        using var capture = new NodeMeterCapture();

        NodeMetrics.TurnToModelLoadStartMs.Record(1234.5, new KeyValuePair<string, object?>("provider", "local"));

        var measurements = capture.Doubles("turn_to_model_load_start_ms");
        AssertEx.Equal(expected: 1, measurements.Count);
        AssertEx.Equal(expected: 1234.5, measurements[0].Value);
        AssertEx.Equal("local", (string?)measurements[0].Tag("provider"));
    }

    [Test]
    public void ModelReadyToFirstOutputMs_RecordsOnNodeMeter_WithProviderTag()
    {
        using var capture = new NodeMeterCapture();

        NodeMetrics.ModelReadyToFirstOutputMs.Record(42.0, new KeyValuePair<string, object?>("provider", "remote"));

        var measurements = capture.Doubles("model_ready_to_first_output_ms");
        AssertEx.Equal(expected: 1, measurements.Count);
        AssertEx.Equal(expected: 42.0, measurements[0].Value);
        AssertEx.Equal("remote", (string?)measurements[0].Tag("provider"));
    }

    [Test]
    public void InvocationCancelledTotal_RecordsOnNodeMeter_WithCategoryTag()
    {
        using var capture = new NodeMeterCapture();

        NodeMetrics.InvocationCancelledTotal.Add(1, new KeyValuePair<string, object?>("category", "user"));
        NodeMetrics.InvocationCancelledTotal.Add(1, new KeyValuePair<string, object?>("category", "operator_eject"));

        AssertEx.Equal(expected: 2L, capture.CountLong("invocation_cancelled_total"));
        AssertEx.Contains(capture.LongTagValues("invocation_cancelled_total", "category"), (object?)"user");
        AssertEx.Contains(capture.LongTagValues("invocation_cancelled_total", "category"), (object?)"operator_eject");
    }

    [Test]
    public void LlamaServerLoadTelemetry_RecordsBoundedPhaseAndPlacementDimensions()
    {
        using var capture = new NodeMeterCapture();
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry();

        telemetry.RecordLoad(new LlamaServerLoadObservation(ModelRole.Chat,
            GpuVariant.Cuda,
            RuntimeVersion: "b10375",
            RuntimeSha256: new string('A', 64),
            ReadinessDurationMs: 1250.5,
            LlamaServerReadinessOutcome.Ready,
            LlamaServerPlacementOutcome.Partial,
            LlamaServerLoadAttemptKind.SafeRetry,
            SpeculativeModeClass.MainModelHeads,
            ModelName: "llama3"));

        var durations = capture.Doubles("llama_server_load_readiness_duration_ms");
        AssertEx.Equal(expected: 1, durations.Count);
        var duration = durations[0];
        AssertEx.Equal(expected: 1250.5, duration.Value);
        AssertEx.Equal("chat", (string?)duration.Tag("role"));
        AssertEx.Equal("cuda", (string?)duration.Tag("variant"));
        AssertEx.Equal("ready", (string?)duration.Tag("outcome"));

        AssertEx.Equal(expected: 1L, capture.CountLong("llama_server_load_total"));
        AssertEx.Contains(capture.LongTagValues("llama_server_load_total", "placement"), (object?)"partial");
        AssertEx.Contains(capture.LongTagValues("llama_server_load_total", "attempt"), (object?)"safe_retry");
        AssertEx.Contains(capture.LongTagValues("llama_server_load_total", "speculation"), (object?)"main_model_heads");
    }

    // A single captured measurement plus a helper to read one of its tag values by key.
    private readonly record struct CapturedMeasurement(double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public object? Tag(string key)
        {
            return Tags.TryGetValue(key, out var value) ? value : null;
        }
    }

    // Captures increments/records of the node meter's instruments for the duration of a test, retaining the tag set of
    // each measurement so both value and dimension can be asserted. Filters to "XE.Node" so unrelated meters are ignored.
    private sealed class NodeMeterCapture : IDisposable
    {
        private readonly ConcurrentBag<(string Name, CapturedMeasurement Measurement)> _doubles = [];
        private readonly ConcurrentBag<(string Name, long Value, IReadOnlyDictionary<string, object?> Tags)> _longs = [];
        private readonly MeterListener _listener = new();

        public NodeMeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, NodeMetrics.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
                _doubles.Add((instrument.Name, new CapturedMeasurement(measurement, ToDictionary(tags)))));
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                _longs.Add((instrument.Name, measurement, ToDictionary(tags))));
            _listener.Start();
        }

        public IReadOnlyList<CapturedMeasurement> Doubles(string instrumentName)
        {
            return _doubles
                   .Where(entry => string.Equals(entry.Name, instrumentName, StringComparison.Ordinal))
                   .Select(entry => entry.Measurement)
                   .ToArray();
        }

        public long CountLong(string instrumentName)
        {
            return _longs.Where(entry => string.Equals(entry.Name, instrumentName, StringComparison.Ordinal)).Sum(entry => entry.Value);
        }

        public IReadOnlyList<object?> LongTagValues(string instrumentName, string tagKey)
        {
            return _longs
                   .Where(entry => string.Equals(entry.Name, instrumentName, StringComparison.Ordinal))
                   .Select(entry => entry.Tags.TryGetValue(tagKey, out var value) ? value : null)
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
