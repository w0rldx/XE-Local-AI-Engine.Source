namespace XE_Local_AI_Engine.Tests.Training.Runs;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Training admission goes straight onto the pending-footprint ledger, never through
///     <c>ICapacityService</c>/<c>CapacityRequest</c>: that record is
///     <c>(ModelName, ModelRole, RequiredContextTokens)</c> and a run has none of the three, so every field would be
///     faked and the down-tier loop behind it would try to admit a smaller model that does not exist. The ledger is
///     also the part that matters — it is the process-wide byte budget every concurrent spawn decision reads.
/// </summary>
public sealed class TrainingCapacityGateTests
{
    private const long OneGigabyte = 1024L * 1024 * 1024;

    [Test]
    public async Task TrainingAdmission_LedgerReservation_NotCapacityRequest()
    {
        using var ledger = new PendingFootprintLedger();
        var capacityService = Substitute.For<ICapacityService>();
        var gate = new TrainingCapacityGate(ledger, Audit(vramBytes: 24 * OneGigabyte));

        using var reservation = await gate.ReserveAsync(Estimate(8 * OneGigabyte));

        AssertEx.True(reservation.Granted, "A run that fits the free budget is admitted.");
        AssertEx.Equal(8 * OneGigabyte, ledger.Reserved.GpuBytes, "The run's bytes must be visible to every other spawn decision.");
        // The capacity service was never consulted: a training footprint cannot be expressed as a CapacityRequest.
        _ = capacityService.DidNotReceiveWithAnyArgs().DecideAsync(default!, default);
    }

    [Test]
    public async Task Reservation_IsReleasedOnDispose()
    {
        using var ledger = new PendingFootprintLedger();
        var gate = new TrainingCapacityGate(ledger, Audit(vramBytes: 24 * OneGigabyte));

        var reservation = await gate.ReserveAsync(Estimate(8 * OneGigabyte));
        reservation.Dispose();

        AssertEx.Equal(expected: 0L, ledger.Reserved.GpuBytes, "A finished run must give its budget back.");
    }

    [Test]
    public async Task Reservation_SeesBytesAlreadyReservedByAnotherSpawn()
    {
        using var ledger = new PendingFootprintLedger();
        var gate = new TrainingCapacityGate(ledger, Audit(vramBytes: 24 * OneGigabyte));
        // An inference spawn was admitted moments ago and its model is still loading, so the live free-VRAM reading
        // does not reflect it yet. This is exactly the window the ledger exists to close.
        using var inflight = ledger.Reserve(new ResourceFootprint(20 * OneGigabyte, RamBytes: 0));

        using var reservation = await gate.ReserveAsync(Estimate(8 * OneGigabyte));

        AssertEx.False(reservation.Granted, "The run must be refused against the ledger, not against the stale snapshot.");
        AssertEx.NotNullOrEmpty(reservation.Reason, "A refusal has to say why.");
    }

    [Test]
    public async Task Reservation_WithoutAUsableGpu_IsRefused()
    {
        using var ledger = new PendingFootprintLedger();
        var gate = new TrainingCapacityGate(ledger, Audit(vramBytes: null));

        using var reservation = await gate.ReserveAsync(Estimate(2 * OneGigabyte));

        AssertEx.False(reservation.Granted, "Training needs measurable CUDA VRAM; a CPU fallback is not a degraded run, it is no run.");
    }

    [Test]
    public async Task Reservation_WithoutEnoughSystemMemory_IsRefused()
    {
        using var ledger = new PendingFootprintLedger();
        var gate = new TrainingCapacityGate(ledger, Audit(vramBytes: 24 * OneGigabyte, availableRamBytes: OneGigabyte));

        using var reservation = await gate.ReserveAsync(Estimate(2 * OneGigabyte));

        AssertEx.False(reservation.Granted, "The trainer process, the tokenized dataset and the dataloader all live in host RAM.");
    }

    private static TrainingFootprintEstimate Estimate(long gpuBytes) =>
        new(gpuBytes, RamBytes: 4 * OneGigabyte, ParameterCount: 8_000_000_000, TrainableParameterCount: 45_000_000, Experimental: false);

    private static IRuntimeDeviceAudit Audit(long? vramBytes, long availableRamBytes = 32 * OneGigabyte)
    {
        var audit = Substitute.For<IRuntimeDeviceAudit>();
        _ = audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                 .Returns(new HardwareProfile
                 {
                     TotalRamBytes = 64 * OneGigabyte,
                     AvailableRamBytes = availableRamBytes,
                     VramBytes = vramBytes,
                     AvailableVramBytes = vramBytes,
                     VramKnown = vramBytes is not null,
                     GpuVendor = vramBytes is null ? GpuVendor.None : GpuVendor.Nvidia,
                     GpuAccelAvailable = vramBytes is not null,
                     CpuCores = 16,
                     FreeDiskBytes = 500 * OneGigabyte
                 });
        return audit;
    }
}
