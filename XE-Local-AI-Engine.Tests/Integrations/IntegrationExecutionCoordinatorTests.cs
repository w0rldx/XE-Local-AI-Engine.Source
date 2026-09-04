namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using Harness = XE_Local_AI_Engine.Tests.Integrations.IntegrationCoordinatorHarness;
using ToolCategory = XE_Local_AI_Engine.AI.Agent.Tools.ToolCategory;

/// <summary>
///     <see cref="IntegrationExecutionCoordinator" />: the package it hands the shared runner, the closed failure
///     vocabulary it writes, the transition table it obeys, the one terminal event and one audit row it produces per
///     execution, the queue-age bound on the lease wait, the lease-before-capacity order, and the startup sweep that
///     mints its recovery event through the buffer rather than by hand.
///     <para>
///         Every test drives <c>ProcessOneAsync</c> (or <c>StartAsync</c>) directly. Starting the hosted loop and
///         writing to the channel would make each assertion a race against a background reader.
///     </para>
/// </summary>
public sealed class IntegrationExecutionCoordinatorTests
{
    private const string SeedText = IntegrationCoordinatorHarness.SeedText;

    [Test]
    public async Task Run_BuildsAnUnattendedPackageOnTheOwnedConversationWithTheSeedTurn()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var package = harness.CapturedPackage ?? throw new AssertionException("The runner was never called.");
        AssertEx.True(package.IsUnattended, "An integration run has no approval round-trip, so IsUnattended must be set.");
        AssertEx.Equal(harness.ConversationId, package.ConversationId, "The package must carry the OWNED conversation id, never a throwaway Guid.");
        AssertEx.Equal(expected: 1, package.ConversationContext.Count, "The seed turn is the whole context of a per-invocation run.");
        AssertEx.Equal(SeedText, package.ConversationContext[0].Content, "The seed turn must be the composed seed read back from the owned conversation.");
        AssertEx.Equal(MessageRole.User, package.ConversationContext[0].Role, "The seed is a user turn.");
    }

    [Test]
    public async Task Run_LeavesApprovalRequiredToolsInTheOffer()
    {
        using var harness = new Harness();
        harness.OfferedTools =
        [
            Tool("read_file", requiresApproval: false, ToolCategory.ReadLocal),
            Tool("run_command", requiresApproval: true, ToolCategory.WriteExecute)
        ];
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var package = harness.CapturedPackage ?? throw new AssertionException("The runner was never called.");
        AssertEx.True(package.AllowedTools.Any(static tool => string.Equals(tool.Name, "run_command", StringComparison.Ordinal)),
            "Approval-required tools must NOT be stripped: silently dropping one degrades the agent where no caller can see it.");
    }

    [Test]
    public async Task Run_WhenTheRunnerReportsAnUnattendedApprovalRefusal_RecordsApprovalRequired()
    {
        using var harness = new Harness();
        harness.TerminalStatus = InvocationStatus.Failed;
        harness.TerminalError = ApprovalUnavailableException.UnattendedReasonPrefix + "run_command";
        harness.TerminalFailureCategory = FailureCategory.AgentRuntime;
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status, "An unattended approval refusal is a terminal failure.");
        AssertEx.Equal(IntegrationFailureCategories.ApprovalRequired,
            row.FailureCategory,
            "The refusal must be distinguishable from a generic fault, which is the whole point of not stripping the tool.");
        AssertEx.Contains(row.FailureSummary ?? string.Empty, "run_command", StringComparison.Ordinal);
    }

    [Test]
    public async Task Run_WhenTheEffectiveModelIsCloud_RejectsBeforeTheRunner()
    {
        using var harness = new Harness();
        harness.Capability.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: true));
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationFailureCategories.CloudModelRejected, row.FailureCategory, "Unattended external work is node-local only.");
        AssertEx.Equal(expected: 0, harness.RunCount, "The locality gate must reject before any invocation.");
        await harness.Dispatcher.DidNotReceive().ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_WhenTheTriggerIsDisabled_RecordsTriggerUnavailable()
    {
        using var harness = new Harness();
        harness.DisableTrigger();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationFailureCategories.TriggerUnavailable, harness.Row(executionId).FailureCategory);
        AssertEx.Equal(expected: 0, harness.RunCount, "A disabled trigger must not reach the runner.");
    }

    [Test]
    public async Task Run_WhenTheOwnedConversationIsMissing_RecordsInternalFailureWithoutRunning()
    {
        using var harness = new Harness();
        harness.HideConversation = true;
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status,
            "An admitted row whose post-commit conversation write failed must terminalize, not hang Accepted.");
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, row.FailureCategory);
        AssertEx.Equal(expected: 0, harness.RunCount, "There is no seed to run against; a run on an empty seed is worse than a clean failure.");
    }

    [Test]
    public async Task Run_WhenCapacityRejects_DisposesTheLeaseAndNeverRuns()
    {
        using var harness = new Harness();
        harness.Capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
               .Returns(new CapacityDecision(CapacityVerdict.RejectInsufficient, "No capacity.", OllamaEvictionWarning: false, Reservation: null));
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationFailureCategories.CapacityRejected, harness.Row(executionId).FailureCategory);
        AssertEx.Equal(expected: 0, harness.RunCount, "A rejected capacity decision must not reach the runner.");
        AssertEx.True(harness.LeaseDisposed, "The lease is taken first now, so a capacity rejection has to dispose it.");
    }

    [Test]
    public async Task Run_TakesTheLeaseBeforeCapacityAndDisposesInReverse()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        // The scheduler holds a GPU footprint reservation across the whole lease wait; reversing the pair is what stops
        // a queued integration run failing a concurrent interactive turn's capacity decision.
        AssertEx.True(harness.LeaseAcquiredOrdinal < harness.CapacityDecidedOrdinal,
            "The invocation lease must be acquired BEFORE the capacity reservation.");
        AssertEx.True(harness.ReservationDisposedOrdinal < harness.LeaseDisposedOrdinal,
            "The two must be disposed in reverse acquisition order: reservation first, then lease.");
    }

    [Test]
    public async Task Run_WhenTheLeaseIsFree_GoesStraightToRunningWithNoQueuedEvent()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.False(harness.Executions.Events.Any(row => row.ExecutionId == executionId
                                                            && string.Equals(row.EventType, IntegrationStreamEventTypes.ExecutionQueued, StringComparison.Ordinal)),
            "Accepted -> Running directly is legal; Queued exists only for a run that actually waits.");
        AssertEx.True(harness.Executions.Events.Any(row => row.ExecutionId == executionId
                                                           && string.Equals(row.EventType, IntegrationStreamEventTypes.ExecutionStarted, StringComparison.Ordinal)),
            "A run that reached the runner must have persisted execution.started.");
    }

    [Test]
    public async Task Run_WhenTheLeaseIsHeld_EmitsQueuedBeforeRunning()
    {
        using var harness = new Harness();
        harness.HoldLease();
        var executionId = harness.SeedAccepted();

        var processing = harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);
        await WaitUntilAsync(() => harness.Row(executionId).Status == IntegrationExecutionStatus.Queued);
        harness.ReleaseLease();
        await processing;

        var events = harness.Executions.Events.Where(row => row.ExecutionId == executionId).OrderBy(static row => row.Sequence).ToArray();
        var queued = Array.FindIndex(events, row => string.Equals(row.EventType, IntegrationStreamEventTypes.ExecutionQueued, StringComparison.Ordinal));
        var started = Array.FindIndex(events, row => string.Equals(row.EventType, IntegrationStreamEventTypes.ExecutionStarted, StringComparison.Ordinal));
        AssertEx.True(queued >= 0, "A run that had to wait for the lease is the only producer of execution.queued.");
        AssertEx.True(queued < started, "execution.queued must precede execution.started.");
    }

    [Test]
    public async Task Run_WhenTheQueuedCasIsLost_AppendsNoQueuedEvent()
    {
        using var harness = new Harness();
        harness.HoldLease();
        // A CAS a concurrent cancel already won on the same version is what this flag reproduces.
        harness.Executions.FailNextStatusCas = true;
        var executionId = harness.SeedAccepted();

        var processing = harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);
        await WaitUntilAsync(() => harness.LeaseRequested);
        harness.ReleaseLease();
        await processing;

        AssertEx.False(harness.Executions.Events.Any(row => row.ExecutionId == executionId
                                                            && string.Equals(row.EventType, IntegrationStreamEventTypes.ExecutionQueued, StringComparison.Ordinal)),
            "No execution.queued may follow a CAS a concurrent writer already won.");
    }

    [Test]
    public async Task Run_WhenTheRowCarriesAStopMarker_CancelsWithoutRunning()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted(stopRequestedAtUtc: 1);

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Cancelled, harness.Row(executionId).Status);
        AssertEx.Equal(expected: 0, harness.RunCount, "A cancel that landed before the row was picked up must never call the runner.");
    }

    [Test]
    public async Task Run_WhenTheRowIsAlreadyCancelled_ProducesNoSecondTerminalEvent()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted(status: IntegrationExecutionStatus.Cancelled);

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(expected: 0,
            harness.Executions.Events.Count(row => row.ExecutionId == executionId),
            "Whoever won the terminal CAS owns the artefacts; the coordinator appends nothing to a row it lost.");
        AssertEx.Empty(harness.Executions.Audits);
    }

    [Test]
    public async Task Run_WhenCancelledDuringTheLeaseWait_RecordsCancelledNotQueueTimeout()
    {
        using var harness = new Harness();
        harness.HoldLease();
        var executionId = harness.SeedAccepted();

        var processing = harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);
        await WaitUntilAsync(() => harness.LeaseRequested);
        AssertEx.True(harness.Cancellations.Signal(executionId), "The coordinator must have registered a cancellation handle before it waits.");
        await processing;

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Cancelled, row.Status, "A cancel and a queue-age expiry both surface as OperationCanceledException and must not be conflated.");
        AssertEx.Null(row.FailureCategory, "A cancel is an outcome, not a failure.");
    }

    [Test]
    public async Task Run_WhenTheRowIsOlderThanTheQueueAge_FailsWithoutTakingTheLease()
    {
        using var harness = new Harness(maxQueueAgeSeconds: 60);
        var executionId = harness.SeedAccepted(receivedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds());

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationFailureCategories.QueueTimeout, harness.Row(executionId).FailureCategory);
        await harness.Dispatcher.DidNotReceive().ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_WhenTheLeaseNeverComes_FailsQueueTimeoutWhileStillWaiting()
    {
        using var harness = new Harness(maxQueueAgeSeconds: 1);
        harness.HoldLease();
        var executionId = harness.SeedAccepted(receivedAtUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // The round-4 shape checked the age once and then awaited with no deadline at all, so this call never returned
        // until the lease came free. The deadline is what makes the advertised maximum queue age observable.
        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationFailureCategories.QueueTimeout, row.FailureCategory);
        AssertEx.Null(row.StartedAtUtc, "A run that never started must not carry a start stamp.");
        AssertEx.False(harness.LeaseAcquired, "The deadline must fire with no lease held.");
        harness.ReleaseLease();
    }

    [Test]
    public async Task Run_WhenTheLeaseArrivesAfterTheDeadline_FailsQueueTimeoutBeforeCapacity()
    {
        // The lease resolves only AFTER the queue-age deadline has passed, and through a delay the deadline token does
        // not reach — which is the exact shape the post-acquisition re-check exists for: a lease that comes free at or
        // past the deadline is still a stale run, and the caller has already been told.
        using var harness = new Harness(maxQueueAgeSeconds: 1);
        harness.LeaseDelay = TimeSpan.FromSeconds(2.5);
        var executionId = harness.SeedAccepted(receivedAtUtc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        AssertEx.Equal(IntegrationFailureCategories.QueueTimeout, harness.Row(executionId).FailureCategory);
        AssertEx.Equal(expected: 0, harness.RunCount, "The post-acquisition re-check runs before capacity and before the runner.");
        await harness.Capacity.DidNotReceive().DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>());
        AssertEx.True(harness.LeaseDisposed, "A stale run must give the node's only invocation slot back.");
    }

    [Test]
    public async Task Run_WhenTheRunnerReportsNoTerminalState_FailsInternally()
    {
        using var harness = new Harness();
        harness.RaiseTerminalState = false;
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, row.FailureCategory, "A runner that returned without reporting is internal-failure, never a wire-visible new category.");
    }

    [Test]
    public async Task Run_WhenTheRunnerReportsFailed_WritesInternalFailureAndKeepsTheEnumNameOutOfTheColumn()
    {
        using var harness = new Harness();
        harness.TerminalStatus = InvocationStatus.Failed;
        harness.TerminalFailureCategory = FailureCategory.ProviderUnreachable;
        harness.TerminalError = "the provider could not be reached";
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, row.FailureCategory, "Only the ten closed categories may reach the column.");
        AssertEx.Contains(row.FailureSummary ?? string.Empty, nameof(FailureCategory.ProviderUnreachable), StringComparison.Ordinal);
    }

    [Test]
    public async Task Run_CreatesTheAssistantPlaceholderBeforeTheRunAndTerminalizesWithAnEnvelope()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.True(harness.PlaceholderOrdinal > 0 && harness.PlaceholderOrdinal < harness.RunOrdinal,
            "Terminalization correlates against an EXISTING placeholder row, so it has to be created before the run.");
        var request = harness.TerminalizeRequest ?? throw new AssertionException("The assistant turn was never terminalized.");
        AssertEx.NotNull(request.Envelope,
            "Without the kind-1 envelope an integration run is the one path invisible to the token-usage view — the kind-3 audit row carries no token columns.");
        AssertEx.Null(request.Parts, "Nothing persists tool events for a plain context, so leaving Parts untouched is the honest answer.");
    }

    [Test]
    public async Task Run_ProducesExactlyOneTerminalEventAndOneAuditRowAtTheHighestSequence()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var events = harness.Executions.Events.Where(row => row.ExecutionId == executionId).OrderBy(static row => row.Sequence).ToArray();
        var terminals = events.Where(static row => row.EventType is IntegrationStreamEventTypes.ExecutionCompleted
                                                       or IntegrationStreamEventTypes.ExecutionFailed
                                                       or IntegrationStreamEventTypes.ExecutionCancelled)
                              .ToArray();
        AssertEx.Equal(expected: 1, terminals.Length, "The coordinator is the only terminal producer, and it produces exactly one.");
        AssertEx.Equal(events[^1].Sequence, terminals[0].Sequence, "A reader stops on the terminal, so it must be the highest sequence for the execution.");
        AssertEx.Equal(expected: 1, harness.Executions.Audits.Count);
    }

    [Test]
    public async Task Run_PersistsEveryEventWithTheSequenceTheBufferMinted()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var sequences = harness.Executions.Events.Where(row => row.ExecutionId == executionId)
                               .Select(static row => row.Sequence)
                               .OrderBy(static sequence => sequence)
                               .ToArray();
        AssertEx.Equal(sequences.Distinct().Count(), sequences.Length, "Two counters would duplicate a sequence and break Last-Event-ID replay.");
        AssertEx.Equal(harness.Buffer.LastSequence(executionId), sequences[^1], "Every persisted sequence comes from the buffer, which is the only minter.");
    }

    [Test]
    public async Task Run_WhenTheTerminalTransactionThrows_PublishesNoCompletionAndFallsBackToInternalFailure()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.Executions.ThrowOnNextTerminalize = true;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        // The single transaction rolls the status back with the event, so the run's own completion is never published.
        // The throw then reaches ProcessOneAsync's handler, which re-reads the row, wins the CAS and closes it — the
        // row IS terminal when the method returns, and leaving it for the next restart would be the worse outcome.
        AssertEx.Equal(long.MaxValue,
            harness.Buffer.LowestPendingReservation(executionId),
            "A reservation that is neither published nor abandoned stalls every reader on this execution.");
        AssertEx.False(harness.Executions.Events.Any(row => row.ExecutionId == executionId
                                                            && string.Equals(row.EventType, IntegrationStreamEventTypes.ExecutionCompleted, StringComparison.Ordinal)),
            "A failed commit publishes nothing.");

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status, "The fault handler terminalizes rather than leaving a slot held against the row forever.");
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, row.FailureCategory);
        AssertEx.Equal(expected: 1,
            harness.Executions.Events.Count(candidate => candidate.ExecutionId == executionId
                                                         && string.Equals(candidate.EventType, IntegrationStreamEventTypes.ExecutionFailed, StringComparison.Ordinal)),
            "One terminal event, written by whoever won the CAS.");
    }

    [Test]
    public async Task Run_WhenTheQueuedTransitionThrowsWhileTheLeaseIsPending_CancelsTheLeaseRequestInsteadOfLeakingThePermit()
    {
        // Disposing the deadline source does NOT cancel a pending SemaphoreSlim wait, so a throw in this window used
        // to leave the node's ONE invocation permit granted to a frame that had already unwound — deadlocking chat,
        // regeneration, the scheduler and every later integration run until the process restarted.
        using var harness = new Harness();
        harness.HoldLease();
        var executionId = harness.SeedAccepted();
        harness.Executions.ThrowOnNextStatusCas = true;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        // Bounded on purpose: a regression parks the request on the still-held gate forever, and a hang is not a
        // failure a suite can report.
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => harness.LeaseRequest.WaitAsync(TimeSpan.FromSeconds(10)),
            "The orphaned lease request must be cancelled, not left to be granted to nobody.");
        AssertEx.False(harness.LeaseAcquired, "No permit may be held once the run that asked for it has unwound.");
        AssertEx.Equal(IntegrationExecutionStatus.Failed, harness.Row(executionId).Status);
        AssertEx.Equal(IntegrationFailureCategories.InternalFailure, harness.Row(executionId).FailureCategory);
    }

    [Test]
    public async Task Run_WhenTheCapacityReservationThrowsOnDispose_StillReleasesTheInvocationLease()
    {
        // Same starvation by a narrower path: the reservation and the lease shared one finally, so a throw from the
        // first skipped the second.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.FailNextReservationDispose = true;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.True(harness.LeaseDisposed, "The node's invocation slot must come back whatever the reservation does.");
    }

    [Test]
    public async Task Run_WhenACancelMarkerBumpsTheVersionMidRun_WinsTheTerminalCasOnTheRetry()
    {
        // The production race deviation 4's single retry exists for: a cancel stamps its durable stop marker on a
        // Running row through UpdateStatusAsync, bumping the version, and the terminal CAS then carries a stale one.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.StampStopMarkerFor = executionId;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Completed, row.Status, "The generation had already finished, so the run's own terminal status is the true one.");
        AssertEx.True(row.StopRequestedAtUtc is not null, "The marker landed mid-run, which is what bumped the version.");
        AssertEx.Equal(expected: 1,
            harness.Executions.Events.Count(candidate => candidate.ExecutionId == executionId && IsTerminalEvent(candidate.EventType)),
            "The retry must close the row, not mint a second terminal event.");
        AssertEx.Equal(expected: 1, harness.Executions.Audits.Count);
    }

    [Test]
    public async Task Run_WhenTheRetriedTerminalCasAlsoLoses_AbandonsTheReservationAndWritesNoAuditRow()
    {
        // The retry is bounded at one. A second loss means another path owns the terminal artefacts, so this one
        // publishes nothing and audits nothing.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.StampStopMarkerFor = executionId;
        harness.Executions.FailNextTerminalizeCas = true;
        harness.Executions.FailSecondTerminalizeCas = true;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(long.MaxValue, harness.Buffer.LowestPendingReservation(executionId), "Every Reserve is resolved, on the loses-twice path too.");
        AssertEx.False(harness.Executions.Events.Any(candidate => candidate.ExecutionId == executionId && IsTerminalEvent(candidate.EventType)));
        AssertEx.Empty(harness.Executions.Audits);
    }

    [Test]
    public async Task Run_WhenTheRunIsCancelled_CancelsTheInvocationByIdAsWellAsSignallingTheToken()
    {
        // B5 asks for both. The linked token stops the generation, but only Cancel() reaches the run's pending tool
        // calls and attributes the turn to the user rather than to a bare abort.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.SignalCancelFor = executionId;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var package = harness.CapturedPackage ?? throw new AssertionException("The runner was never called.");
        harness.Runner.Received(requiredNumberOfCalls: 1).Cancel(package.InvocationId);
    }

    [Test]
    public async Task StartAsync_SkipsRowsAdmittedAfterTheCoordinatorWasConstructed()
    {
        // Kestrel is registered during builder construction and starts before this coordinator, so a request can be
        // admitted while the sweep is still paging. Terminalizing it would contradict a 202 the caller already holds.
        using var harness = new Harness();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var interrupted = harness.SeedAccepted(receivedAtUtc: now - 600_000);
        var admittedDuringStartup = harness.SeedAccepted(receivedAtUtc: now + 600_000);

        await harness.Coordinator.StartAsync(CancellationToken.None);
        await harness.Coordinator.StopAsync(CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Failed, harness.Row(interrupted).Status, "A row the previous process left behind is not resumable.");
        AssertEx.Equal(IntegrationFailureCategories.Restart, harness.Row(interrupted).FailureCategory);
        AssertEx.Equal(IntegrationExecutionStatus.Accepted, harness.Row(admittedDuringStartup).Status,
            "A row admitted after this coordinator existed is live, and the accept path already enqueued it.");
        AssertEx.False(harness.Executions.Events.Any(candidate => candidate.ExecutionId == admittedDuringStartup),
            "The sweep must not write a terminal event for a run that has not started yet.");
    }

    [Test]
    public async Task Run_WhenTheTerminalCasIsLost_AbandonsTheReservationAndWritesNoAuditRow()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.Executions.FailNextTerminalizeCas = true;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(long.MaxValue, harness.Buffer.LowestPendingReservation(executionId), "Every Reserve is resolved, on the lost path too.");
        AssertEx.Empty(harness.Executions.Audits);
    }

    [Test]
    public async Task Run_WritesOnlyCategoriesFromTheClosedVocabulary()
    {
        using var harness = new Harness();
        harness.TerminalStatus = InvocationStatus.Failed;
        harness.TerminalFailureCategory = FailureCategory.Unexpected;
        var ran = harness.SeedAccepted();
        var stale = harness.SeedAccepted(receivedAtUtc: DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds());

        await harness.Coordinator.ProcessOneAsync(ran, CancellationToken.None);
        await harness.Coordinator.ProcessOneAsync(stale, CancellationToken.None);

        foreach (var category in harness.Executions.Rows.Select(static row => row.FailureCategory).Where(static value => value is not null))
        {
            AssertEx.True(IntegrationFailureCategories.All.Contains(category!),
                $"'{category}' is outside the closed failure vocabulary S2's stream contract and S4's chips are written against.");
        }
    }

    [Test]
    public async Task StartAsync_TerminalizesInterruptedRowsAtTheirOwnNextSequence()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted(status: IntegrationExecutionStatus.Running, lastSequence: 7);

        await harness.Coordinator.StartAsync(CancellationToken.None);
        await harness.Coordinator.StopAsync(CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status, "V1 does not resume in-flight generations.");
        AssertEx.Equal(IntegrationFailureCategories.Restart, row.FailureCategory);
        var recovered = harness.Executions.Events.Single(candidate => candidate.ExecutionId == executionId);
        AssertEx.Equal(expected: 8L, recovered.Sequence, "TryCreate seeds the ring from the persisted watermark, so the sweep mints LastSequence + 1 through the one authority.");
        AssertEx.True(harness.Buffer.IsTracked(executionId), "The recovered entry has to be tracked, or a reader gets a 404 for a row that exists.");
    }

    [Test]
    public async Task StartAsync_VisitsEachInterruptedRowExactlyOnce()
    {
        using var harness = new Harness();
        var accepted = harness.SeedAccepted();
        var queued = harness.SeedAccepted(status: IntegrationExecutionStatus.Queued);
        var running = harness.SeedAccepted(status: IntegrationExecutionStatus.Running);

        await harness.Coordinator.StartAsync(CancellationToken.None);
        await harness.Coordinator.StopAsync(CancellationToken.None);

        foreach (var executionId in new[] { accepted, queued, running })
        {
            AssertEx.Equal(expected: 1,
                harness.Executions.Events.Count(row => row.ExecutionId == executionId),
                "One sweep, one terminal event per interrupted row.");
        }
    }

    [Test]
    public async Task ExecuteAsync_WhileAnotherHolderOwnsTheLease_MovesEveryQueuedExecutionToQueued()
    {
        // Live F1: the reader awaited ProcessOneAsync, so the second id sat in the channel for the whole of the first
        // run. It never reached Queued and never started measuring its queue age.
        using var harness = new Harness();
        harness.HoldLeaseSlot();
        var first = harness.SeedLive();
        var second = harness.SeedLive();

        await harness.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            await harness.Queue.Writer.WriteAsync(first);
            await harness.Queue.Writer.WriteAsync(second);

            await WaitUntilAsync(() => harness.Row(first).Status == IntegrationExecutionStatus.Queued
                                       && harness.Row(second).Status == IntegrationExecutionStatus.Queued);
        }
        finally
        {
            harness.ReleaseLeaseSlot();
            await harness.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task ExecuteAsync_WhileAnotherHolderOwnsTheLease_TimesTheSecondExecutionOutOnItsOwnDeadline()
    {
        // The second execution must WAIT on the lease and time out there — not sit in the channel and get failed by
        // the pre-check the moment the run ahead of it finishes, which is what the live round recorded at 227 s.
        using var harness = new Harness(maxQueueAgeSeconds: 1);
        harness.HoldLeaseSlot();
        var first = harness.SeedLive();
        var second = harness.SeedLive();

        await harness.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            await harness.Queue.Writer.WriteAsync(first);
            await harness.Queue.Writer.WriteAsync(second);

            await WaitUntilAsync(() => harness.Row(second).Status == IntegrationExecutionStatus.Failed);
            var row = harness.Row(second);
            AssertEx.Equal(IntegrationFailureCategories.QueueTimeout, row.FailureCategory);
            AssertEx.True(row.StartedAtUtc is null, "A run that never got the lease never started.");
            AssertEx.True(harness.Executions.Events.Any(candidate => candidate.ExecutionId == second
                                                                     && string.Equals(candidate.EventType, IntegrationStreamEventTypes.ExecutionQueued, StringComparison.Ordinal)),
                "Queued is the proof it was waiting on the lease rather than on the channel reader.");
        }
        finally
        {
            harness.ReleaseLeaseSlot();
            await harness.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenOneDispatchedTaskFaults_KeepsDequeuingTheRest()
    {
        // A store read that throws before the run's own handler is in scope escapes ProcessOneAsync. On the reader
        // loop that killed the loop, and with it every later execution on the node.
        using var harness = new Harness();
        var faulting = harness.SeedLive();
        var healthy = harness.SeedLive();

        await harness.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            harness.Executions.ThrowOnNextGetById = true;
            await harness.Queue.Writer.WriteAsync(faulting);
            await WaitUntilAsync(() => !harness.Executions.ThrowOnNextGetById);

            await harness.Queue.Writer.WriteAsync(healthy);
            await WaitUntilAsync(() => harness.Row(healthy).Status == IntegrationExecutionStatus.Completed);
            await WaitUntilAsync(() => harness.Row(faulting).Status != IntegrationExecutionStatus.Accepted);

            AssertEx.Equal(IntegrationExecutionStatus.Completed, harness.Row(faulting).Status,
                "One transient read failure is retried, so the run still happens.");
        }
        finally
        {
            await harness.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StopAsync_WithSeveralExecutionsWaitingOnTheLease_TerminalizesEveryOneAsShutdown()
    {
        using var harness = new Harness();
        harness.HoldLeaseSlot();
        var first = harness.SeedLive();
        var second = harness.SeedLive();

        await harness.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            await harness.Queue.Writer.WriteAsync(first);
            await harness.Queue.Writer.WriteAsync(second);
            await WaitUntilAsync(() => harness.Row(first).Status == IntegrationExecutionStatus.Queued
                                       && harness.Row(second).Status == IntegrationExecutionStatus.Queued);

            // ExecuteAsync awaits its dispatched tasks, so StopAsync does not return until both have terminalized.
            await harness.Coordinator.StopAsync(CancellationToken.None);

            foreach (var executionId in new[] { first, second })
            {
                var row = harness.Row(executionId);
                AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
                AssertEx.Equal(IntegrationFailureCategories.Shutdown, row.FailureCategory, "A run stopped by the host is a shutdown, not a queue timeout.");
            }
        }
        finally
        {
            harness.ReleaseLeaseSlot();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenEveryDispatchAttemptFaults_TerminalizesTheRowInsteadOfStrandingIt()
    {
        // The id has already left the channel, so nothing else will ever pick this execution up. Leaving it Accepted
        // held one of its principal's admission slots until the process restarted, and the default per-principal cap
        // is two: two such faults answered every later invoke from that integrator with a 503.
        using var harness = new Harness();
        var stranded = harness.SeedLive();

        await harness.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            // Exactly the retry budget: every attempt faults, and the read the terminalization itself does succeeds.
            harness.Executions.ThrowOnGetByIdCount = 3;
            await harness.Queue.Writer.WriteAsync(stranded);

            await WaitUntilAsync(() => harness.Row(stranded).Status == IntegrationExecutionStatus.Failed);
            AssertEx.Equal(IntegrationFailureCategories.InternalFailure, harness.Row(stranded).FailureCategory);
            AssertEx.True(harness.Executions.Events.Any(candidate => candidate.ExecutionId == stranded && IsTerminalEvent(candidate.EventType)),
                "A terminalized row owes its caller a terminal event.");
        }
        finally
        {
            await harness.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_WhenTheWholeSweepKeepsFailing_FailsHostStartRatherThanServingWithWedgedRows()
    {
        // The sweep used to swallow this and start anyway, leaving every interrupted row non-terminal AND still
        // counted against its principal's cap. McpAgentRunRecoveryService's shape: log critical, rethrow, let the
        // supervisor restart.
        using var harness = new Harness();
        _ = harness.SeedAccepted();
        harness.Executions.ThrowOnEveryList = true;

        _ = await AssertEx.ThrowsAsync<Exception>(() => harness.Coordinator.StartAsync(CancellationToken.None)).ConfigureAwait(false);
    }

    [Test]
    public async Task StartAsync_WhenAnEventSitsAboveTheRowWatermark_SeedsRecoveryFromTheEventNotTheWatermark()
    {
        // The lost-update this fix closes leaves exactly this row behind: a watermark of 2 with a committed event at 4.
        // Seeding the ring at 2 mints the terminal at 3 and then collides with the existing row at 4 on EVERY restart,
        // so the execution can never terminalize and holds its admission slot for good.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted(lastSequence: 2);
        harness.Buffer.Remove(executionId);
        await harness.Executions.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(),
            executionId,
            Sequence: 4,
            IntegrationStreamEventTypes.ToolCompleted,
            DetailJson: null,
            OccurredAtUtc: 1));

        await harness.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            var row = harness.Row(executionId);
            AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
            AssertEx.Equal(IntegrationFailureCategories.Restart, row.FailureCategory);

            var terminal = harness.Executions.Events.Single(candidate => candidate.ExecutionId == executionId && IsTerminalEvent(candidate.EventType));
            AssertEx.Equal(expected: 5L, terminal.Sequence, "The recovery terminal continues from the highest PERSISTED sequence, not from the stale watermark.");
        }
        finally
        {
            await harness.Coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Run_WhenTheTargetAgentIsAnOrchestrator_RefusesItAsTriggerUnavailable()
    {
        // Ruling D2 scopes V1 to a saved single agent, and this package carries no orchestration spec: an orchestrator
        // would report Completed having run none of its participants. Checked at run time as well as at save, because
        // a definition's Kind can change after the trigger was written.
        using var harness = new Harness
        {
            DefinitionKind = AgentDefinitionKind.Orchestrator
        };
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal(IntegrationFailureCategories.TriggerUnavailable, row.FailureCategory);
        AssertEx.Contains(row.FailureSummary ?? string.Empty, "orchestrator");
        AssertEx.Equal(expected: 0, harness.RunCount, "An orchestrator target must never reach the runner as a lone agent.");
    }

    [Test]
    public async Task Run_WhenAStopMarkerLandsBeforeTheRunningCas_TerminalizesCancelledRatherThanFailed()
    {
        // The cancel path stamps its durable marker as a NON-terminal status update, which bumps the version and makes
        // the coordinator's Running CAS lose. Reloading the row and ignoring the marker terminalized it
        // Failed/internal-failure and beat the cancel to it, so a caller that got a 202 from the cancel endpoint was
        // then shown a failure it never asked for.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.Executions.BeforeNextStatusCas = () => harness.Executions.StampStopMarker(executionId, stopRequestedAtUtc: 4_242);

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Cancelled, row.Status);
        AssertEx.Null(row.FailureCategory, "A cancel is an outcome, not a failure.");
        AssertEx.Equal(expected: 0, harness.RunCount, "The row never reached Running, so nothing ran.");
    }

    [Test]
    public async Task Run_WhenTheRunIsCancelledMidGeneration_TerminalizesCancelledRatherThanInternalFailure()
    {
        // The run token is linked to the cancel token, so a real runner surfaces the cancel as an
        // OperationCanceledException. Classifying that as internal-failure told the caller its request broke when it
        // had done exactly what was asked.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.DuringRun = (self, package) =>
        {
            AssertEx.NotNull(package);
            _ = self.Cancellations.Signal(executionId);
            throw new OperationCanceledException();
        };

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Cancelled, row.Status);
        AssertEx.Null(row.FailureCategory);
        AssertEx.True(harness.Executions.Events.Any(candidate => candidate.ExecutionId == executionId
                                                                 && string.Equals(candidate.EventType, IntegrationStreamEventTypes.ExecutionCancelled, StringComparison.Ordinal)),
            "A cancelled run publishes execution.cancelled, never execution.failed.");
    }

    private static bool IsTerminalEvent(string eventType) =>
        string.Equals(eventType, IntegrationStreamEventTypes.ExecutionCompleted, StringComparison.Ordinal)
        || string.Equals(eventType, IntegrationStreamEventTypes.ExecutionFailed, StringComparison.Ordinal)
        || string.Equals(eventType, IntegrationStreamEventTypes.ExecutionCancelled, StringComparison.Ordinal);

    /// <summary>Polls a coordinator state a background await produces, so a test never sleeps for a fixed guess.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new AssertionException("The awaited coordinator state never arrived.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }

    private static AllowedToolDto Tool(string name, bool requiresApproval, ToolCategory category) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            RequiresApproval = requiresApproval,
            Category = category
        };
}
