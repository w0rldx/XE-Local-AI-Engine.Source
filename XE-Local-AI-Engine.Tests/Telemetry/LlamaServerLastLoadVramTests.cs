namespace XE_Local_AI_Engine.Tests.Telemetry;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The meter bridge's second job: remembering the VRAM figures of the LAST SUCCESSFUL load per (model, role), which
///     is what lets a node run settling minutes later say what the box looked like when its model was loaded.
/// </summary>
public sealed class LlamaServerLastLoadVramTests
{
    [Test]
    public void RecordLoad_ReadyLoad_IsRememberedForItsOwnModelAndRole()
    {
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry();

        telemetry.RecordLoad(Observation("llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Ready, globalFree: 7_000, admitted: 5_000));

        var remembered = AssertEx.NotNull(telemetry.TryGetLastReadyLoad("llama-3.1", ModelRole.Chat));
        AssertEx.Equal(expected: 7_000L, remembered.GlobalFreeVramBytesAtLoad);
        AssertEx.Equal(expected: 5_000L, remembered.AdmittedVramBytes);

        AssertEx.Null(telemetry.TryGetLastReadyLoad("llama-3.1", ModelRole.Embedding), "The embedding server is a different process with its own load.");
        AssertEx.Null(telemetry.TryGetLastReadyLoad("mistral", ModelRole.Chat), "And a model nobody loaded has nothing to report.");
    }

    /// <summary>Model names are compared the way every other (model, role) key in the runtime compares them.</summary>
    [Test]
    public void TryGetLastReadyLoad_MatchesTheModelNameCaseInsensitively()
    {
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry();
        telemetry.RecordLoad(Observation("Llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Ready, globalFree: 7_000, admitted: 5_000));

        AssertEx.NotNull(telemetry.TryGetLastReadyLoad("llama-3.1", ModelRole.Chat));
    }

    /// <summary>
    ///     A failed or cancelled attempt never became the model that served anything, so it must not overwrite the
    ///     reading of the load that did — nor create one where none existed.
    /// </summary>
    [Test]
    public void RecordLoad_NonReadyAttempt_LeavesTheLastSuccessfulReadingAlone()
    {
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry();
        telemetry.RecordLoad(Observation("llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Ready, globalFree: 7_000, admitted: 5_000));

        telemetry.RecordLoad(Observation("llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Failed, globalFree: 11, admitted: 22));
        telemetry.RecordLoad(Observation("mistral", ModelRole.Chat, LlamaServerReadinessOutcome.Cancelled, globalFree: 33, admitted: 44));

        var remembered = AssertEx.NotNull(telemetry.TryGetLastReadyLoad("llama-3.1", ModelRole.Chat));
        AssertEx.Equal(expected: 7_000L, remembered.GlobalFreeVramBytesAtLoad, "The failed retry's numbers describe a process that never served.");
        AssertEx.Null(telemetry.TryGetLastReadyLoad("mistral", ModelRole.Chat), "And a cancelled load leaves no reading behind at all.");
    }

    /// <summary>Each successful load overwrites its key: a reload under different pressure legitimately reads differently.</summary>
    [Test]
    public void RecordLoad_SecondReadyLoad_ReplacesTheEarlierReading()
    {
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry();
        telemetry.RecordLoad(Observation("llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Ready, globalFree: 7_000, admitted: 5_000));

        telemetry.RecordLoad(Observation("llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Ready, globalFree: 2_000, admitted: 1_500));

        var remembered = AssertEx.NotNull(telemetry.TryGetLastReadyLoad("llama-3.1", ModelRole.Chat));
        AssertEx.Equal(expected: 2_000L, remembered.GlobalFreeVramBytesAtLoad);
        AssertEx.Equal(expected: 1_500L, remembered.AdmittedVramBytes);
    }

    /// <summary>An unadmitted load measured nothing, so there is nothing worth remembering under its key.</summary>
    [Test]
    public void RecordLoad_ReadyLoadThatMeasuredNothing_IsNotRemembered()
    {
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry();

        telemetry.RecordLoad(Observation("llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Ready, globalFree: null, admitted: null));

        AssertEx.Null(telemetry.TryGetLastReadyLoad("llama-3.1", ModelRole.Chat));
    }

    /// <summary>
    ///     An unadmitted reload measured nothing, but it still REPLACED the process the earlier reading described, so
    ///     the key is cleared rather than left reporting bytes for a process that no longer exists. Contrast
    ///     <see cref="RecordLoad_NonReadyAttempt_LeavesTheLastSuccessfulReadingAlone" />: a failed attempt never
    ///     replaced anything, so it leaves the admitted reading standing.
    /// </summary>
    [Test]
    public void RecordLoad_UnadmittedReadyReload_ClearsTheEarlierReading()
    {
        var telemetry = new NodeMetricsLlamaServerLoadTelemetry();
        telemetry.RecordLoad(Observation("llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Ready, globalFree: 7_000, admitted: 5_000));

        telemetry.RecordLoad(Observation("llama-3.1", ModelRole.Chat, LlamaServerReadinessOutcome.Ready, globalFree: null, admitted: null));

        AssertEx.Null(telemetry.TryGetLastReadyLoad("llama-3.1", ModelRole.Chat),
            "A direct, profiling or variant-moved reload carries no admission, and the old process it replaced is gone.");
    }

    private static LlamaServerLoadObservation Observation(string modelName,
        ModelRole role,
        LlamaServerReadinessOutcome outcome,
        long? globalFree,
        long? admitted) =>
        new(role,
            GpuVariant.Cuda,
            RuntimeVersion: "b10375",
            RuntimeSha256: null,
            ReadinessDurationMs: 1_000,
            outcome,
            LlamaServerPlacementOutcome.Full,
            LlamaServerLoadAttemptKind.Primary,
            SpeculativeModeClass.Disabled,
            modelName,
            globalFree,
            admitted);
}
