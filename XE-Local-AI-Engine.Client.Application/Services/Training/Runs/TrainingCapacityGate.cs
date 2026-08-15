namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>A granted reservation, or a refusal carrying the reason. Disposing releases the reserved bytes.</summary>
public sealed record TrainingCapacityReservation(bool Granted, string? Reason, IDisposable? Handle) : IDisposable
{
    public void Dispose() =>
        Handle?.Dispose();
}

/// <summary>
///     Admission for a training run's footprint.
/// </summary>
/// <remarks>
///     Deliberately NOT routed through <c>ICapacityService</c>: a <c>CapacityRequest</c> is
///     <c>(ModelName, ModelRole, RequiredContextTokens)</c> and a training run has no model identity, no role and no
///     context window — every field would have to be faked, and the down-tier loop behind it would then "helpfully"
///     admit a smaller model that does not exist. The reservation goes straight onto
///     <see cref="IPendingFootprintLedger" />, which is the part that actually matters: it is the process-wide byte
///     budget every concurrent spawn decision reads, so a run's bytes are visible to an inference spawn and vice versa.
/// </remarks>
public interface ITrainingCapacityGate
{
    Task<TrainingCapacityReservation> ReserveAsync(TrainingFootprintEstimate estimate, CancellationToken cancellationToken = default);
}

public sealed class TrainingCapacityGate(IPendingFootprintLedger ledger, IRuntimeDeviceAudit deviceAudit) : ITrainingCapacityGate
{
    private readonly IRuntimeDeviceAudit _deviceAudit = deviceAudit ?? throw new ArgumentNullException(nameof(deviceAudit));
    private readonly IPendingFootprintLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    public async Task<TrainingCapacityReservation> ReserveAsync(TrainingFootprintEstimate estimate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        // The decision gate serializes read-decide-reserve so two admissions cannot both pass on the same snapshot.
        using (await _ledger.EnterDecisionAsync(cancellationToken).ConfigureAwait(false))
        {
            var profile = await _deviceAudit.GetEffectiveProfileAsync(forceRefreshProfile: true, cancellationToken).ConfigureAwait(false);
            if (!profile.VramKnown)
            {
                return new TrainingCapacityReservation(Granted: false, "No usable GPU was detected on this node.", Handle: null);
            }

            var reserved = _ledger.Reserved;
            var freeVram = (profile.AvailableVramBytes ?? 0) - reserved.GpuBytes;
            if (estimate.GpuBytes > freeVram)
            {
                return new TrainingCapacityReservation(Granted: false,
                    "Not enough free VRAM to start this run. Eject any loaded model and try again.",
                    Handle: null);
            }

            var freeRam = profile.AvailableRamBytes - reserved.RamBytes;
            if (estimate.RamBytes > freeRam)
            {
                return new TrainingCapacityReservation(Granted: false, "Not enough free system memory to start this run.", Handle: null);
            }

            return new TrainingCapacityReservation(Granted: true,
                Reason: null,
                _ledger.Reserve(new ResourceFootprint(estimate.GpuBytes, estimate.RamBytes)));
        }
    }
}
