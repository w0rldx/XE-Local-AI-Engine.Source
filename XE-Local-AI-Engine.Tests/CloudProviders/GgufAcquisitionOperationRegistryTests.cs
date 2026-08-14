namespace XE_Local_AI_Engine.Tests.CloudProviders;

using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class GgufAcquisitionOperationRegistryTests
{
    [Test]
    public void Start_SameActiveKindAndModel_RejoinsStableOperation()
    {
        var registry = new GgufAcquisitionOperationRegistry(TimeProvider.System);

        var first = registry.Start(GgufAcquisitionOperationKind.Download, "model:Q4_K_M");
        var second = registry.Start(GgufAcquisitionOperationKind.Download, "MODEL:q4_k_m");

        AssertEx.False(first.AlreadyInFlight);
        AssertEx.True(second.AlreadyInFlight);
        AssertEx.Equal(first.Status.OperationId, second.Status.OperationId);
        AssertEx.NotEqual(Guid.Empty, first.Status.OperationId);
    }

    [Test]
    public void Start_SameModelDifferentKinds_AreIndependentAndKindFiltered()
    {
        var registry = new GgufAcquisitionOperationRegistry(TimeProvider.System);

        var download = registry.Start(GgufAcquisitionOperationKind.Download, "model:Q4_K_M");
        var import = registry.Start(GgufAcquisitionOperationKind.Import, "model:Q4_K_M");

        AssertEx.NotEqual(download.Status.OperationId, import.Status.OperationId);
        AssertEx.Equal(expected: 1, registry.List(GgufAcquisitionOperationKind.Download).Count);
        AssertEx.Equal(expected: 1, registry.List(GgufAcquisitionOperationKind.Import).Count);
        AssertEx.Equal(import.Status.OperationId, registry.GetStatus(import.Status.OperationId)!.OperationId);
    }

    [Test]
    public void TerminalUpdate_IsRetainedButNoLongerCancellable()
    {
        var registry = new GgufAcquisitionOperationRegistry(TimeProvider.System);
        var registration = registry.Start(GgufAcquisitionOperationKind.Import, "model:Q4_K_M", totalBytes: 42);

        var terminal = registry.Update(registration.Status.OperationId,
            GgufAcquisitionPhase.Completed,
            completedBytes: 42,
            totalBytes: 42);

        AssertEx.Equal(GgufAcquisitionPhase.Completed, terminal.Phase);
        AssertEx.Equal(expected: 42L, terminal.CompletedBytes);
        AssertEx.False(registry.Cancel(registration.Status.OperationId));
        AssertEx.Equal(terminal, registry.GetStatus(registration.Status.OperationId));
    }

    [Test]
    public void Cancel_ActiveOperation_SignalsDetachedToken()
    {
        var registry = new GgufAcquisitionOperationRegistry(TimeProvider.System);
        var registration = registry.Start(GgufAcquisitionOperationKind.Import, "model:Q4_K_M");

        var cancelled = registry.Cancel(registration.Status.OperationId);

        AssertEx.True(cancelled);
        AssertEx.True(registration.CancellationToken.IsCancellationRequested);
    }

    [Test]
    public void LateProgress_CannotRegressTerminalStatus()
    {
        var registry = new GgufAcquisitionOperationRegistry(TimeProvider.System);
        var registration = registry.Start(GgufAcquisitionOperationKind.Import, "model:Q4_K_M", totalBytes: 42);
        _ = registry.Update(registration.Status.OperationId, GgufAcquisitionPhase.Completed, completedBytes: 42, totalBytes: 42);

        var afterLateProgress = registry.Update(registration.Status.OperationId,
            GgufAcquisitionPhase.Running,
            completedBytes: 21,
            totalBytes: 42);

        AssertEx.Equal(GgufAcquisitionPhase.Completed, afterLateProgress.Phase);
        AssertEx.Equal(expected: 42L, afterLateProgress.CompletedBytes);
        AssertEx.False(registry.Cancel(registration.Status.OperationId));
    }

    [Test]
    public void TerminalRetention_CountCapEvictsOldestTerminalButNeverActive()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new GgufAcquisitionOperationRegistry(time, TimeSpan.FromHours(1), maxTerminalCount: 2);
        var active = registry.Start(GgufAcquisitionOperationKind.Download, "active:Q4_K_M");
        var first = registry.RecordTerminal(GgufAcquisitionOperationKind.Download, "first:Q4_K_M", GgufAcquisitionPhase.Completed);
        var second = registry.RecordTerminal(GgufAcquisitionOperationKind.Import, "second:Q4_K_M", GgufAcquisitionPhase.Failed);
        var third = registry.RecordTerminal(GgufAcquisitionOperationKind.Download, "third:Q4_K_M", GgufAcquisitionPhase.Cancelled);

        AssertEx.True(registry.GetStatus(first.OperationId) is null);
        AssertEx.NotNull(registry.GetStatus(second.OperationId));
        AssertEx.NotNull(registry.GetStatus(third.OperationId));
        AssertEx.NotNull(registry.GetStatus(active.Status.OperationId));
        AssertEx.True(registry.Cancel(active.Status.OperationId));
    }

    [Test]
    public void TerminalRetention_MaxAgeExpiresTerminalButNeverActive()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new GgufAcquisitionOperationRegistry(time, TimeSpan.FromMinutes(5), maxTerminalCount: 256);
        var terminal = registry.RecordTerminal(GgufAcquisitionOperationKind.Download, "terminal:Q4_K_M", GgufAcquisitionPhase.Completed);
        var active = registry.Start(GgufAcquisitionOperationKind.Import, "active:Q4_K_M");

        time.Advance(TimeSpan.FromMinutes(6));

        AssertEx.True(registry.GetStatus(terminal.OperationId) is null);
        AssertEx.NotNull(registry.GetStatus(active.Status.OperationId));
    }

    [Test]
    public void Updates_AreStrictlyMonotonicWhenClockDoesNotAdvanceOrMovesBackward()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch.AddHours(1));
        var registry = new GgufAcquisitionOperationRegistry(time);
        var registration = registry.Start(GgufAcquisitionOperationKind.Download, "model:Q4_K_M");
        var downloading = registry.Update(registration.Status.OperationId, GgufAcquisitionPhase.Downloading);
        time.Advance(TimeSpan.FromHours(-1));
        var committing = registry.Update(registration.Status.OperationId, GgufAcquisitionPhase.Committing);
        var completed = registry.Update(registration.Status.OperationId, GgufAcquisitionPhase.Completed);

        AssertEx.True(registration.Status.UpdatedAtUtc < downloading.UpdatedAtUtc);
        AssertEx.True(downloading.UpdatedAtUtc < committing.UpdatedAtUtc);
        AssertEx.True(committing.UpdatedAtUtc < completed.UpdatedAtUtc);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() =>
            _utcNow;

        public void Advance(TimeSpan elapsed) =>
            _utcNow += elapsed;
    }
}
