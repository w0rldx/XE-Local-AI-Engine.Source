namespace XE_Local_AI_Engine.Tests.Integrations;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cancel primitive both the operator surface and the external route call. Two properties carry it: a cancel
///     that has decided to stop a run finishes stamping and closing it whatever the caller's connection does, and a
///     lost marker CAS answers what the row actually reads rather than a blanket 202.
/// </summary>
public sealed class IntegrationExecutionQueryServiceTests
{
    [Test]
    public async Task RequestCancel_WhenTheCallerDisconnects_StillStampsTerminalizesAuditsAndSignals()
    {
        // The route hands this method RequestAborted. On the caller's token the marker write, the terminal transaction
        // and the audit all abort, and the OperationCanceledException escapes before the signal — leaving a queued row
        // parked in its lease wait until MaxQueueAgeSeconds, because the durable marker is only honoured post-lease.
        using var harness = new Harness();
        var executionId = harness.Seed(IntegrationExecutionStatus.Queued);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var outcome = await harness.Service.RequestCancelAsync(executionId, aborted.Token);

        AssertEx.Equal(IntegrationCancelOutcome.Requested, outcome);
        var row = harness.Row(executionId);
        AssertEx.True(row.StopRequestedAtUtc is not null, "The durable stop marker is what a restart reads.");
        AssertEx.Equal(IntegrationExecutionStatus.Cancelled, row.Status, "A row that never started is terminalized by the cancel itself.");
        AssertEx.True(harness.Executions.Events.Any(candidate => candidate.ExecutionId == executionId
                                                                 && string.Equals(candidate.EventType, IntegrationStreamEventTypes.ExecutionCancelled, StringComparison.Ordinal)),
            "Whoever wins the terminal CAS owns the terminal event.");
        AssertEx.Equal(expected: 1, harness.Executions.Audits.Count);
        AssertEx.True(harness.CancelTokenFired, "The registered run must be signalled on every path.");
    }

    [Test]
    public async Task RequestCancel_WhenTheMarkerCasLosesToATerminalRow_Answers409NotAccepted()
    {
        using var harness = new Harness();
        var executionId = harness.Seed(IntegrationExecutionStatus.Running);

        // The row terminalized between this cancel's read and its compare-and-swap, so the CAS loses on both the
        // version and the expected status.
        harness.Executions.BeforeNextStatusCas = () => harness.Terminalize(executionId);

        var outcome = await harness.Service.RequestCancelAsync(executionId);

        AssertEx.Equal(IntegrationCancelOutcome.AlreadyTerminal, outcome, "A cancel on a finished run is a 409, not a 202 the caller will wait on.");
        AssertEx.True(harness.CancelTokenFired, "The signal still fires: it costs nothing and a run that is finishing must not be missed.");
    }

    [Test]
    public async Task RequestCancel_WhenTheMarkerCasLosesToAStillLiveRow_StillAnswersAccepted()
    {
        using var harness = new Harness();
        var executionId = harness.Seed(IntegrationExecutionStatus.Queued);

        // The coordinator won the Queued -> Running race on the same version a moment earlier.
        harness.Executions.FailNextStatusCas = true;

        var outcome = await harness.Service.RequestCancelAsync(executionId);

        AssertEx.Equal(IntegrationCancelOutcome.Requested, outcome, "The row is still live, and the signal is what stops it.");
        AssertEx.True(harness.CancelTokenFired);
    }

    [Test]
    public async Task RequestCancel_OnARowWhoseMarkerIsAlreadyStamped_WritesNothingASecondTime()
    {
        // Every cancel used to re-stamp the marker under a fresh version. A caller hammering the endpoint drifted the
        // row's version out from under the coordinator's bounded terminal retries and stranded it Running.
        using var harness = new Harness();
        var executionId = harness.Seed(IntegrationExecutionStatus.Running);

        AssertEx.Equal(IntegrationCancelOutcome.Requested, await harness.Service.RequestCancelAsync(executionId));
        var afterFirst = harness.Row(executionId);
        AssertEx.True(afterFirst.StopRequestedAtUtc is not null);
        AssertEx.Equal(expected: 1L, afterFirst.Version);

        AssertEx.Equal(IntegrationCancelOutcome.Requested, await harness.Service.RequestCancelAsync(executionId),
            "The marker is already durable and the signal still fires, so the answer is unchanged.");
        var afterSecond = harness.Row(executionId);
        AssertEx.Equal(expected: 1L, afterSecond.Version, "A second cancel must cost the row no version at all.");
        AssertEx.Equal(afterFirst.StopRequestedAtUtc, afterSecond.StopRequestedAtUtc);
    }

    [Test]
    public async Task RequestCancel_WhenItsOwnTerminalCasLosesToATerminalRow_Answers409NotAccepted()
    {
        using var harness = new Harness();
        var executionId = harness.Seed(IntegrationExecutionStatus.Accepted);

        // A pre-run rejection reached the row between this cancel's marker write and its own terminal CAS.
        harness.Executions.BeforeTerminalizeCas = _ => harness.Executions.Fail(executionId, IntegrationFailureCategories.TriggerUnavailable);

        var outcome = await harness.Service.RequestCancelAsync(executionId);

        AssertEx.Equal(IntegrationCancelOutcome.AlreadyTerminal, outcome,
            "The run is over: a 202 would have the caller poll for a cancellation that will never arrive.");
        AssertEx.True(harness.CancelTokenFired);
    }

    private sealed class Harness : IDisposable
    {
        private readonly IntegrationExecutionEventBuffer _buffer;
        private readonly IntegrationCancellationRegistry _cancellations = new();
        private readonly Guid _triggerId = Guid.NewGuid();
        private CancellationToken _cancelToken;
        private Guid _registered;

        public Harness()
        {
            _buffer = new IntegrationExecutionEventBuffer(Options.Create(new IntegrationOptions()), TimeProvider.System);
            Service = new IntegrationExecutionQueryService(Executions,
                Triggers,
                _buffer,
                _cancellations,
                TimeProvider.System,
                NullLogger<IntegrationExecutionQueryService>.Instance);
        }

        public bool CancelTokenFired => _cancelToken.IsCancellationRequested;

        public FakeIntegrationExecutionStore Executions { get; } = new();

        public IntegrationExecutionQueryService Service { get; }

        public FakeIntegrationTriggerStore Triggers { get; } = new();

        public void Dispose()
        {
            _buffer.Dispose();
            _cancellations.Remove(_registered);
        }

        public IntegrationExecutionSnapshot Row(Guid executionId) =>
            Executions.Rows.Single(row => row.Id == executionId);

        public Guid Seed(IntegrationExecutionStatus status)
        {
            var executionId = Guid.NewGuid();
            _ = Executions.Seed(executionId, _triggerId, Guid.NewGuid(), status);

            // The coordinator registers before it waits, which is what makes step 3's signal observable at all.
            AssertEx.True(_cancellations.TryRegister(executionId, out _cancelToken));
            _registered = executionId;
            return executionId;
        }

        /// <summary>Closes the row the way the coordinator would, so a cancel arriving a moment later loses its CAS.</summary>
        public void Terminalize(Guid executionId)
        {
            var row = Row(executionId);
            AssertEx.True(_buffer.TryCreate(executionId, row.LastSequence));
            _ = Executions.TryTerminalizeAsync(new IntegrationTerminalizeCommand(executionId,
                                  row.Version,
                                  new HashSet<IntegrationExecutionStatus>
                                  {
                                      row.Status
                                  },
                                  IntegrationExecutionStatus.Completed,
                                  _buffer.Reserve(executionId),
                                  IntegrationStreamEventTypes.ExecutionCompleted,
                                  EndedAtUtc: 9,
                                  FailureCategory: null,
                                  FailureSummary: null),
                              CancellationToken.None)
                          .GetAwaiter()
                          .GetResult();
        }
    }
}
