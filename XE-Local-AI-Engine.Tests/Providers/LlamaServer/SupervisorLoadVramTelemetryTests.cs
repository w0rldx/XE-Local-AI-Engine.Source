namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The load observation carries the two VRAM figures the capacity gate already knew — the free-VRAM reading it took
///     under its decision gate and the GPU bytes it reserved — and carries neither when the spawn had no admission to
///     read them from. Nothing here probes the device: an unadmitted spawn reports nulls rather than a measurement.
/// </summary>
public sealed class SupervisorLoadVramTelemetryTests
{
    private const long GlobalFreeBytes = 7_340_032_000L;
    private const long AdmittedBytes = 5_368_709_120L;

    [Test]
    public async Task EnsureRunning_WithAdmission_PutsTheAdmittedVramFiguresOnTheObservation()
    {
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var registry = new ProcessLaunchAdmissionRegistry();
        using var consumer = registry.Acquire(Admission("model-a", GlobalFreeBytes));
        AssertEx.NotNull(consumer);
        await using var supervisor = SupervisorFactory.Create(variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            launchAdmissions: registry,
            loadTelemetry: telemetry);

        var endpoint = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.NotNull(endpoint);
        var observation = telemetry.Observations.Single();
        AssertEx.Equal(LlamaServerReadinessOutcome.Ready, observation.Outcome);
        AssertEx.Equal("model-a", observation.ModelName, "The model has to travel with the observation or nothing can key on it.");
        AssertEx.Equal(GlobalFreeBytes, observation.GlobalFreeVramBytesAtLoad, "The free-VRAM reading the capacity gate took is carried, not re-measured.");
        AssertEx.Equal(AdmittedBytes, observation.AdmittedVramBytes, "And the GPU bytes that admission reserved for this process.");
    }

    [Test]
    public async Task EnsureRunning_WithoutAdmission_LeavesBothVramFiguresNull()
    {
        var telemetry = new FakeLlamaServerLoadTelemetry();
        await using var supervisor = SupervisorFactory.Create(variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            loadTelemetry: telemetry);

        var endpoint = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.NotNull(endpoint);
        var observation = telemetry.Observations.Single();
        AssertEx.Equal(LlamaServerReadinessOutcome.Ready, observation.Outcome);
        AssertEx.Null(observation.GlobalFreeVramBytesAtLoad, "A direct spawn was never admitted, so there is no reading to report — and none is invented.");
        AssertEx.Null(observation.AdmittedVramBytes, "Nor any reservation.");
    }

    /// <summary>
    ///     A gate that read no global-free figure — a non-NVIDIA or CPU-only host — still admits, and the observation
    ///     says so with a null rather than a zero that would claim the device was full.
    /// </summary>
    [Test]
    public async Task EnsureRunning_WhenAdmissionMeasuredNoFreeVram_ReportsNullFreeAndTheReservationStill()
    {
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var registry = new ProcessLaunchAdmissionRegistry();
        using var consumer = registry.Acquire(Admission("model-a", globalFreeVramBytes: null));
        AssertEx.NotNull(consumer);
        await using var supervisor = SupervisorFactory.Create(variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            launchAdmissions: registry,
            loadTelemetry: telemetry);

        _ = await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var observation = telemetry.Observations.Single();
        AssertEx.Null(observation.GlobalFreeVramBytesAtLoad, "Unmeasured is not zero.");
        AssertEx.Equal(AdmittedBytes, observation.AdmittedVramBytes, "The reservation is known whether or not the free-VRAM read succeeded.");
    }

    private static ProcessLaunchAdmission Admission(string modelName, long? globalFreeVramBytes) =>
        new(modelName,
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            new ProcessContextAllocation(8192,
                ModelTrainContextTokens: 131072,
                ProcessContextAllocationSource.HardwareTier,
                ProcessPlacementMode.GpuResident,
                new ResourceFootprint(AdmittedBytes, RamBytes: 0),
                ContentIdentity: $"{modelName}:0",
                CacheKey: $"cache:{modelName}"),
            globalFreeVramBytes);
}
