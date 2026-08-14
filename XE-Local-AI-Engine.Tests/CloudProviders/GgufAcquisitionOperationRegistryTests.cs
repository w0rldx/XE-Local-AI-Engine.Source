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
}
