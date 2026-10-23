namespace XE_Local_AI_Engine.Tests.Telemetry;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The in-memory perf record behind the cold-load reason and the support bundle's <c>perf.json</c>: last ready load and unload reason
///     per (model, role), fed through the load-telemetry bridge, and a bounded ring of recent turn throughput.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalModelPerfLogTests
{
    [Test]
    public void TheTelemetryBridge_RecordsTheReadyLoadDuration_ThenTheUnloadReason_PerModelAndRole()
    {
        var perfLog = new LocalModelPerfLog(new AdvanceableTimeProvider());
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry(perfLog);

        telemetry.RecordLoad(Observation("Llama-3.1", LlamaServerReadinessOutcome.Ready, readinessMs: 9_000));
        telemetry.RecordUnload("llama-3.1", ModelRole.Chat, LlamaServerUnloadReason.IdleTimeout);

        var history = AssertEx.NotNull(perfLog.TryGetLoadHistory("LLAMA-3.1", ModelRole.Chat), "Model names compare case-insensitively.");
        AssertEx.Equal(expected: 9_000d, history.LastReadyLoadMs);
        AssertEx.Equal(LlamaServerUnloadReason.IdleTimeout, history.LastUnloadReason);
        AssertEx.Null(perfLog.TryGetLoadHistory("llama-3.1", ModelRole.Embedding), "The embedder is a different process.");
    }

    [Test]
    public void AReadyLoad_ClearsThePreviousUnloadReason_AndAFailedLoadLeavesTheHistoryAlone()
    {
        var perfLog = new LocalModelPerfLog(new AdvanceableTimeProvider());
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry(perfLog);
        telemetry.RecordLoad(Observation("llama-3.1", LlamaServerReadinessOutcome.Ready, readinessMs: 9_000));
        telemetry.RecordUnload("llama-3.1", ModelRole.Chat, LlamaServerUnloadReason.LoadedModelCap);

        telemetry.RecordLoad(Observation("llama-3.1", LlamaServerReadinessOutcome.Failed, readinessMs: 50));
        AssertEx.Equal(LlamaServerUnloadReason.LoadedModelCap, perfLog.TryGetLoadHistory("llama-3.1", ModelRole.Chat)?.LastUnloadReason);

        telemetry.RecordLoad(Observation("llama-3.1", LlamaServerReadinessOutcome.Ready, readinessMs: 4_000));
        var history = AssertEx.NotNull(perfLog.TryGetLoadHistory("llama-3.1", ModelRole.Chat));
        AssertEx.Null(history.LastUnloadReason, "The reload's process is still loaded, so the old reason no longer describes it.");
        AssertEx.Equal(expected: 4_000d, history.LastReadyLoadMs);
    }

    [Test]
    public void RecordTurn_KeepsTheNewestTwentyTurns_AndSkipsATurnWithoutThroughput()
    {
        var perfLog = new LocalModelPerfLog(new AdvanceableTimeProvider());

        perfLog.RecordTurn("cloud-model", new InvocationThroughput());
        perfLog.RecordTurn("cloud-model", throughput: null);
        for (var turn = 1; turn <= LocalModelPerfLog.MaxTurns + 2; turn++)
        {
            perfLog.RecordTurn("llama-3.1", new InvocationThroughput { GenerationTokens = turn, GenerationMs = 100, SegmentCount = 1 });
        }

        var turns = perfLog.Snapshot().Turns;
        AssertEx.Equal(LocalModelPerfLog.MaxTurns, turns.Count);
        AssertEx.Equal(expected: 3, turns[0].GenerationTokens, "The two oldest turns were dropped, oldest first.");
        AssertEx.Equal(LocalModelPerfLog.MaxTurns + 2, turns[^1].GenerationTokens);
    }

    private static LlamaServerLoadObservation Observation(string modelName, LlamaServerReadinessOutcome outcome, double readinessMs) =>
        new()
        {
            Role = ModelRole.Chat,
            Variant = GpuVariant.Cuda,
            RuntimeVersion = "b10375",
            RuntimeSha256 = null,
            ReadinessDurationMs = readinessMs,
            Outcome = outcome,
            Placement = LlamaServerPlacementOutcome.Full,
            AttemptKind = LlamaServerLoadAttemptKind.Primary,
            SpeculativeModeClass = SpeculativeModeClass.Disabled,
            ModelName = modelName,
            GlobalFreeVramBytesAtLoad = null,
            AdmittedVramBytes = null
        };
}
