namespace XE_Local_AI_Engine.Tests.Agents;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behaviour of <see cref="ToolApprovalAuditRecorder" /> (OPP-03): each resolved decision increments the content-free
///     <c>tool_approval_decisions_total</c> counter tagged by category + decision, opens a scope to append the metadata-
///     only audit row, and swallows any store failure so the approval round-trip can never break. Serial because the
///     counter capture reads a process-global meter.
/// </summary>
[NotInParallel]
public sealed class ToolApprovalAuditRecorderTests
{
    [Test]
    public async Task RecordAsync_IncrementsCounterWithTags_AndForwardsReusedFieldsToStore()
    {
        var store = Substitute.For<IAgentExecutionLogStore>();
        ApprovalDecisionAuditInput? captured = null;
        _ = store.AddApprovalDecisionAsync(Arg.Do<ApprovalDecisionAuditInput>(input => captured = input), Arg.Any<CancellationToken>());

        var recorder = new ToolApprovalAuditRecorder(BuildScopeFactory(store), NullLogger<ToolApprovalAuditRecorder>.Instance);
        var invocationId = Guid.NewGuid();

        using var capture = new NodeMeterCapture();

        await recorder.RecordAsync(invocationId,
            "search_web",
            ToolCategory.Network,
            ApprovalDecisions.Deny,
            ApprovalDecisionSources.Hub,
            latencyMs: 1_234L);

        // Counter incremented once, tagged by the ToolCategory name and the decision.
        AssertEx.Equal(expected: 1L, capture.CountLong("tool_approval_decisions_total"));
        AssertEx.Contains(capture.LongTagValues("tool_approval_decisions_total", "category"), (object?)"Network");
        AssertEx.Contains(capture.LongTagValues("tool_approval_decisions_total", "decision"), (object?)ApprovalDecisions.Deny);

        // The reused fields reach the store as a metadata-only input (category rendered as the enum name).
        var input = AssertEx.NotNull(captured);
        AssertEx.Equal(invocationId, input.InvocationId);
        AssertEx.Equal("search_web", input.ToolName);
        AssertEx.Equal("Network", input.Category);
        AssertEx.Equal(ApprovalDecisions.Deny, input.Decision);
        AssertEx.Equal(ApprovalDecisionSources.Hub, input.Source);
        AssertEx.Equal(expected: 1_234L, input.LatencyMs);
    }

    [Test]
    public async Task RecordAsync_WhenStoreThrows_SwallowsAndStillIncrementsCounter()
    {
        var store = Substitute.For<IAgentExecutionLogStore>();
        _ = store.AddApprovalDecisionAsync(Arg.Any<ApprovalDecisionAuditInput>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromException(new InvalidOperationException("audit store down")));

        var recorder = new ToolApprovalAuditRecorder(BuildScopeFactory(store), NullLogger<ToolApprovalAuditRecorder>.Instance);

        using var capture = new NodeMeterCapture();

        // Must NOT throw — a failing audit write can never break the approval round-trip.
        await recorder.RecordAsync(Guid.NewGuid(),
            "spawn_subagent",
            ToolCategory.Orchestration,
            ApprovalDecisions.Approve,
            ApprovalDecisionSources.Local,
            latencyMs: 5L);

        // The content-free counter is incremented independently of the (failed) durable write.
        AssertEx.Equal(expected: 1L, capture.CountLong("tool_approval_decisions_total"));
        AssertEx.Contains(capture.LongTagValues("tool_approval_decisions_total", "decision"), (object?)ApprovalDecisions.Approve);
    }

    // A DI container exposing the (substituted) log store as scoped, so the recorder's per-decision CreateAsyncScope +
    // GetRequiredService path resolves it exactly as it does in production.
    private static IServiceScopeFactory BuildScopeFactory(IAgentExecutionLogStore store)
    {
        var provider = new ServiceCollection()
                       .AddScoped(_ => store)
                       .BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    // Captures long-counter increments on the "XE.Node" meter with their tag sets for the duration of a test.
    private sealed class NodeMeterCapture : IDisposable
    {
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
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                _longs.Add((instrument.Name, measurement, ToDictionary(tags))));
            _listener.Start();
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
