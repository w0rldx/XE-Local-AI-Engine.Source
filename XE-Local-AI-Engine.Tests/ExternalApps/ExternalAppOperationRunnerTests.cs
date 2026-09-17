namespace XE_Local_AI_Engine.Tests.ExternalApps;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The admission-versus-execution split: what a caller gets back, what happens to the work when the caller goes
///     away, and what a second caller is told while the first is still running.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppOperationRunnerTests
{
    [Test]
    public async Task Install_ReturnsTheAdmittedSnapshot_WhileThePullIsStillBlocked()
    {
        await using var harness = await NewHarnessAsync().ConfigureAwait(false);
        harness.Gated.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = await harness.Service.InstallAsync(Command()).ConfigureAwait(false);

        // The call returned. The pull has not.
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, admitted.Status);
        AssertEx.Equal(expected: 0L, admitted.Version);
        await harness.Gated.PullReached.Task.WaitAsync(TestBudgets.Contended).ConfigureAwait(false);

        var row = AssertEx.NotNull(await harness.ReadAsync(admitted.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, row.Status);

        _ = harness.Gated.PullGate.TrySetResult();
        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);
    }

    /// <summary>
    ///     The request's own token never reaches the pipeline. A browser that navigates away mid-install must not
    ///     leave half-created containers behind with nothing left to settle the row.
    /// </summary>
    [Test]
    public async Task Install_WhenTheCallersTokenIsCancelled_KeepsRunning()
    {
        await using var harness = await NewHarnessAsync().ConfigureAwait(false);
        harness.Gated.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var callerCancellation = new CancellationTokenSource();
        var admitted = await harness.Service.InstallAsync(Command(), callerCancellation.Token).ConfigureAwait(false);

        await harness.Gated.PullReached.Task.WaitAsync(TestBudgets.Contended).ConfigureAwait(false);
        await callerCancellation.CancelAsync().ConfigureAwait(false);
        _ = harness.Gated.PullGate.TrySetResult();

        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);
        AssertEx.Equal(ExternalAppDesiredState.Running, row.DesiredState);
    }

    [Test]
    public async Task ASecondCommandWhileTheRunnerHoldsTheGate_Is409OperationInFlight()
    {
        await using var harness = await NewHarnessAsync().ConfigureAwait(false);
        harness.Gated.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = await harness.Service.InstallAsync(Command()).ConfigureAwait(false);
        await harness.Gated.PullReached.Task.WaitAsync(TestBudgets.Contended).ConfigureAwait(false);

        // The gate is keyed by instance, so the second attempt at the same APPLICATION is refused by the
        // one-per-application rule rather than by the gate; both are 409s a caller can act on.
        _ = await AssertEx.ThrowsAsync<ExternalAppAlreadyInstalledException>(() => harness.Service.InstallAsync(Command())).ConfigureAwait(false);

        AssertEx.True(harness.Runner.IsRunning(admitted.Id), "The install must still hold its instance entry.");

        _ = harness.Gated.PullGate.TrySetResult();
        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);
    }

    /// <summary>
    ///     The gate the install took at admission is held by the BACKGROUND operation, not by the request. That is
    ///     what makes a reconciler or an observer skip a row while it is being installed.
    /// </summary>
    [Test]
    public async Task WhileAnInstallRuns_TheInstanceGateIsHeldAndCannotBeEnteredElsewhere()
    {
        await using var harness = await NewHarnessAsync().ConfigureAwait(false);
        harness.Gated.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = await harness.Service.InstallAsync(Command()).ConfigureAwait(false);
        await harness.Gated.PullReached.Task.WaitAsync(TestBudgets.Contended).ConfigureAwait(false);

        var key = ExternalAppInstanceGate.InstanceKey(admitted.Id);
        AssertEx.Null(await harness.Gate.TryEnterAsync(key).ConfigureAwait(false),
            "A reconcile pass must not be able to take a gate a live install is standing behind.");

        _ = harness.Gated.PullGate.TrySetResult();
        _ = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running).ConfigureAwait(false);

        using var afterwards = AssertEx.NotNull(await harness.Gate.TryEnterAsync(key).ConfigureAwait(false),
            "The lease is released in the operation's finally, which SettleAsync has already waited for.");
    }

    [Test]
    public async Task Cancel_DuringAPull_SettlesToFailedAndKeepsTheStorage()
    {
        await using var harness = await NewHarnessAsync().ConfigureAwait(false);
        harness.Gated.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = await harness.Service.InstallAsync(Command()).ConfigureAwait(false);
        await harness.Gated.PullReached.Task.WaitAsync(TestBudgets.Contended).ConfigureAwait(false);

        AssertEx.True(harness.Runner.Cancel(admitted.Id), "An install that is running has an entry to cancel.");

        var row = await harness.SettleAsync(admitted.Id, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);
        AssertEx.Contains(row.FailureSummary, "cancelled");
        AssertEx.Empty(harness.Runtime.CreatedContainerIds, "A cancel during the pull creates nothing.");
    }

    /// <summary>
    ///     Shutdown is not a cancel. The pipeline stops, the row keeps its transient status for the boot reconciler
    ///     to settle, and NO container is touched: the user's applications keep serving while the engine is down.
    /// </summary>
    [Test]
    public async Task ApplicationStopping_CancelsInFlightOperationsAndLeavesTheRowTransient()
    {
        await using var harness = await NewHarnessAsync().ConfigureAwait(false);
        harness.Gated.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var admitted = await harness.Service.InstallAsync(Command()).ConfigureAwait(false);
        await harness.Gated.PullReached.Task.WaitAsync(TestBudgets.Contended).ConfigureAwait(false);

        harness.StopHost();
        await harness.WaitUntilIdleAsync(admitted.Id).ConfigureAwait(false);

        var row = AssertEx.NotNull(await harness.ReadAsync(admitted.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, row.Status);
        AssertEx.Null(row.FailureCategory);
        AssertEx.Empty(harness.Runtime.RemovedContainerIds, "A shutdown must not remove anything.");
    }

    [Test]
    public async Task Cancel_WithNoRunningOperation_ReportsNothingToStop()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var runner = new ExternalAppOperationRunner(provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHostApplicationLifetime>(),
            NullLogger<ExternalAppOperationRunner>.Instance);

        AssertEx.False(runner.Cancel(Guid.NewGuid()), "Nothing is running, so there is nothing to cancel.");
    }

    private static async Task<ExternalAppServiceHarness> NewHarnessAsync()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web", storage: [new ApplicationStorage("data", "/var/lib/app")])]);

        return await ExternalAppServiceHarness.CreateAsync(manifest).ConfigureAwait(false);
    }

    private static InstallCommand Command()
    {
        return new InstallCommand("test-app",
            DisplayName: null,
            ManifestVersion: 1,
            new string('0', 64),
            new Dictionary<string, string>(StringComparer.Ordinal),
            AcceptPermissions: true);
    }
}
