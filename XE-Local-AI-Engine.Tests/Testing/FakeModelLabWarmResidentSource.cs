namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>A settable warm Model Lab worker for residents and eject tests; counts the ejects it was asked for.</summary>
internal sealed class FakeModelLabWarmResidentSource : IModelLabWarmResidentSource
{
    private int _ejectCalls;

    public ModelLabWarmResident? Resident { get; set; }

    /// <summary>What <see cref="EjectAsync" /> answers; a <see cref="ModelLabEjectOutcome.Gone" /> answer also clears <see cref="Resident" />.</summary>
    public ModelLabEjectOutcome EjectResult { get; set; } = ModelLabEjectOutcome.Gone;

    public int EjectCalls => Volatile.Read(ref _ejectCalls);

    public ModelLabWarmResident? GetResident() => Resident;

    public Task<ModelLabEjectOutcome> EjectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _ejectCalls);
        if (EjectResult == ModelLabEjectOutcome.Gone)
        {
            Resident = null;
        }

        return Task.FromResult(EjectResult);
    }
}
