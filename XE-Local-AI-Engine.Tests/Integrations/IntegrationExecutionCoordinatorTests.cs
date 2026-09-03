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
    private const string EffectiveLocalModel = "local-default-model";

    private const string SeedText = "Summarize the overnight sensor readings.";

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
        await harness.AuditLog.DidNotReceive().AddIntegrationInvocationAsync(Arg.Any<IntegrationInvocationAuditInput>(), Arg.Any<CancellationToken>());
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
        await harness.AuditLog.Received(requiredNumberOfCalls: 1).AddIntegrationInvocationAsync(Arg.Any<IntegrationInvocationAuditInput>(), Arg.Any<CancellationToken>());
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
    public async Task Run_WhenTheTerminalTransactionThrows_PublishesNothingAndLeavesTheRowNonTerminal()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.Executions.ThrowOnNextTerminalize = true;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        // The single transaction rolls the status back with the event, so the row stays visible to the next restart
        // sweep — the property the split write could not deliver.
        AssertEx.Equal(long.MaxValue,
            harness.Buffer.LowestPendingReservation(executionId),
            "A reservation that is neither published nor abandoned stalls every reader on this execution.");
        AssertEx.False(harness.Executions.Events.Any(row => row.ExecutionId == executionId
                                                            && string.Equals(row.EventType, IntegrationStreamEventTypes.ExecutionCompleted, StringComparison.Ordinal)),
            "A failed commit publishes nothing.");
    }

    [Test]
    public async Task Run_WhenTheTerminalCasIsLost_AbandonsTheReservationAndWritesNoAuditRow()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();
        harness.Executions.FailNextTerminalizeCas = true;

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(long.MaxValue, harness.Buffer.LowestPendingReservation(executionId), "Every Reserve is resolved, on the lost path too.");
        await harness.AuditLog.DidNotReceive().AddIntegrationInvocationAsync(Arg.Any<IntegrationInvocationAuditInput>(), Arg.Any<CancellationToken>());
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

    private sealed class Harness : IDisposable
    {
        private readonly IntegrationExecutionEventBuffer _buffer;
        private readonly Guid _agentDefinitionId = Guid.NewGuid();
        private readonly FakeIntegrationTriggerStore _triggers = new();
        private readonly FakeIntegrationSessionStore _sessions = new();
        private readonly TrackingDisposable _reservation;
        private readonly TrackingAsyncDisposable _lease;
        private readonly ServiceProvider _provider;
        private readonly IntegrationTriggerSnapshot _trigger;
        private TaskCompletionSource _leaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ordinal;

        public Harness(int maxQueueAgeSeconds = 120)
        {
            MaxQueueAgeSeconds = maxQueueAgeSeconds;
            _reservation = new TrackingDisposable(this);
            _lease = new TrackingAsyncDisposable(this);
            _leaseGate.SetResult();

            _trigger = _triggers.Seed("sensor-ingest", _agentDefinitionId);
            SessionId = Guid.NewGuid();
            ConversationId = Guid.NewGuid();
            _ = _sessions.Seed(SessionId, _trigger.Id, ConversationId, _agentDefinitionId);

            Definitions.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(BuildDefinition());
            NodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
            LocalDefault.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(EffectiveLocalModel);
            Capability.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                      .Returns(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false));
            Resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                    .Returns(_ => new ResolvedAgentRuntime("SCAFFOLD+PERSONA", OfferedTools, ModelProfile: null, "medium", AgentDefinitionVersion: 7, _agentDefinitionId, "Sensor agent", []));
            Capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                    .Returns(_ =>
                    {
                        CapacityDecidedOrdinal = Next();
                        return new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false, _reservation);
                    });
            Dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                      .Returns(callInfo => AcquireLeaseAsync(callInfo.Arg<CancellationToken>()));
            Persistence.GetConversationForTurnAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => BuildConversation());
            Persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>())
                       .Returns(callInfo =>
                       {
                           PlaceholderOrdinal = Next();
                           var request = callInfo.Arg<NodeChatCreateAssistantPlaceholderRequest>();
                           return Message(request.MessageId, "assistant", string.Empty);
                       });
            Persistence.TerminalizeAssistantMessageAsync(Arg.Any<NodeChatTerminalizeMessageRequest>(), Arg.Any<CancellationToken>())
                       .Returns(callInfo =>
                       {
                           TerminalizeRequest = callInfo.Arg<NodeChatTerminalizeMessageRequest>();
                           return Message(TerminalizeRequest.Correlation.MessageId, "assistant", TerminalizeRequest.Content ?? string.Empty);
                       });
            UsageProviders.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("llamacpp");
            Runner.When(runner => runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()))
                  .Do(callInfo =>
                  {
                      RunOrdinal = Next();
                      RunCount++;
                      var package = callInfo.Arg<InvocationExecutionContext>().Package;
                      CapturedPackage = package;
                      if (!RaiseTerminalState)
                      {
                          return;
                      }

                      // Raised from inside RunAsync, exactly as the dispatcher does in production.
                      Dispatcher.InvocationStateChanged += Raise.EventWith(new InvocationStateChangedEventArgs(new InvocationState
                      {
                          InvocationId = package.InvocationId,
                          ConversationId = package.ConversationId,
                          Status = TerminalStatus,
                          Error = TerminalError,
                          FailureCategory = TerminalFailureCategory,
                          ModelUsed = EffectiveLocalModel,
                          StartedAt = DateTimeOffset.UnixEpoch,
                          CompletedAt = DateTimeOffset.UnixEpoch,
                          GenerationDurationMs = 12
                      }));
                  });

            var options = Options.Create(new IntegrationOptions());
            _buffer = new IntegrationExecutionEventBuffer(options, TimeProvider.System);

            var services = new ServiceCollection();
            services.AddSingleton<IIntegrationExecutionStore>(Executions);
            services.AddSingleton<IIntegrationSessionStore>(_sessions);
            services.AddSingleton<IIntegrationTriggerStore>(_triggers);
            services.AddSingleton(Definitions);
            services.AddSingleton(Resolver);
            services.AddSingleton(Capability);
            services.AddSingleton(LocalDefault);
            services.AddSingleton(NodeSettings);
            services.AddSingleton(Capacity);
            services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
            services.AddSingleton(Runner);
            services.AddSingleton(Dispatcher);
            services.AddSingleton(Persistence);
            services.AddSingleton(AuditLog);
            services.AddSingleton(UsageProviders);
            _provider = services.BuildServiceProvider();

            Coordinator = new IntegrationExecutionCoordinator(_provider.GetRequiredService<IServiceScopeFactory>(),
                Channel.CreateBounded<Guid>(8),
                _buffer,
                Cancellations,
                new QueueAgeOptions(this),
                TimeProvider.System,
                NullLogger<IntegrationExecutionCoordinator>.Instance);
        }

        public IntegrationExecutionCoordinator Coordinator { get; }

        public FakeIntegrationExecutionStore Executions { get; } = new();

        public IntegrationCancellationRegistry Cancellations { get; } = new();

        public IIntegrationExecutionEventBuffer Buffer => _buffer;

        public IAgentDefinitionStore Definitions { get; } = Substitute.For<IAgentDefinitionStore>();

        public IAgentDefinitionResolver Resolver { get; } = Substitute.For<IAgentDefinitionResolver>();

        public IModelCapabilityResolver Capability { get; } = Substitute.For<IModelCapabilityResolver>();

        public ILocalDefaultChatModelResolver LocalDefault { get; } = Substitute.For<ILocalDefaultChatModelResolver>();

        public INodeSettingsStore NodeSettings { get; } = Substitute.For<INodeSettingsStore>();

        public ICapacityService Capacity { get; } = Substitute.For<ICapacityService>();

        public IInvocationRunner Runner { get; } = Substitute.For<IInvocationRunner>();

        public IWorkerEventDispatcher Dispatcher { get; } = Substitute.For<IWorkerEventDispatcher>();

        public INodeChatPersistenceService Persistence { get; } = Substitute.For<INodeChatPersistenceService>();

        public IAgentExecutionLogStore AuditLog { get; } = Substitute.For<IAgentExecutionLogStore>();

        public IUsageProviderResolver UsageProviders { get; } = Substitute.For<IUsageProviderResolver>();

        public Guid SessionId { get; }

        public Guid ConversationId { get; }

        public int MaxQueueAgeSeconds { get; }

        public TimeSpan LeaseDelay { get; set; } = TimeSpan.Zero;

        public IReadOnlyList<AllowedToolDto> OfferedTools { get; set; } = [];

        public bool HideConversation { get; set; }

        public bool RaiseTerminalState { get; set; } = true;

        public InvocationStatus TerminalStatus { get; set; } = InvocationStatus.Completed;

        public string? TerminalError { get; set; }

        public FailureCategory? TerminalFailureCategory { get; set; }

        public RuntimePackage? CapturedPackage { get; private set; }

        public NodeChatTerminalizeMessageRequest? TerminalizeRequest { get; private set; }

        public int RunCount { get; private set; }

        public int RunOrdinal { get; private set; }

        public int PlaceholderOrdinal { get; private set; }

        public int CapacityDecidedOrdinal { get; private set; }

        public int LeaseAcquiredOrdinal { get; private set; }

        public int LeaseDisposedOrdinal { get; private set; }

        public int ReservationDisposedOrdinal { get; private set; }

        public bool LeaseRequested { get; private set; }

        public bool LeaseAcquired { get; private set; }

        public bool LeaseDisposed => LeaseDisposedOrdinal > 0;

        public Guid SeedAccepted(IntegrationExecutionStatus status = IntegrationExecutionStatus.Accepted,
            long? receivedAtUtc = null,
            long lastSequence = 1,
            long version = 0,
            long? stopRequestedAtUtc = null)
        {
            var executionId = Guid.NewGuid();
            _ = Executions.Seed(executionId,
                _trigger.Id,
                SessionId,
                status,
                receivedAtUtc ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                lastSequence,
                version,
                stopRequestedAtUtc);
            _ = _buffer.TryCreate(executionId, lastSequence);
            return executionId;
        }

        public IntegrationExecutionSnapshot Row(Guid executionId) =>
            Executions.Rows.Single(row => row.Id == executionId);

        public void DisableTrigger() =>
            _triggers.Disable(_trigger.Id);

        public void HoldLease() =>
            _leaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseLease() =>
            _leaseGate.TrySetResult();

        public void Dispose()
        {
            _buffer.Dispose();
            _reservation.Dispose();
            _provider.Dispose();
        }

        internal int Next() =>
            Interlocked.Increment(ref _ordinal);

        internal void RecordLeaseDisposed() =>
            LeaseDisposedOrdinal = Next();

        internal void RecordReservationDisposed() =>
            ReservationDisposedOrdinal = Next();

        private async Task<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken)
        {
            LeaseRequested = true;
            await _leaseGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (LeaseDelay > TimeSpan.Zero)
            {
                // Deliberately NOT linked to the deadline token: the lease has to arrive LATE, not be cancelled.
                await Task.Delay(LeaseDelay, CancellationToken.None).ConfigureAwait(false);
            }

            LeaseAcquired = true;
            LeaseAcquiredOrdinal = Next();
            return _lease;
        }

        private NodeChatConversationDto? BuildConversation()
        {
            if (HideConversation)
            {
                return null;
            }

            var seeds = Executions.Rows.Select(row => Message(row.Id, "user", SeedText)).ToArray();
            return new NodeChatConversationDto(ConversationId,
                "sensor-ingest",
                UserId: null,
                CreatedAtUtc: 0,
                LastSeenUtc: 0,
                Purged: false,
                seeds);
        }

        private static NodeChatPersistedMessageDto Message(Guid messageId, string role, string content) =>
            new(messageId,
                Guid.Empty,
                RequestId: null,
                Sequence: 0,
                role,
                content,
                Reasoning: null,
                NodeChatMessageStatusValues.Completed,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                Model: null,
                Error: null,
                MetadataJson: null);

        private AgentDefinitionRecord BuildDefinition() =>
            new(_agentDefinitionId,
                "Sensor agent",
                Description: null,
                Instructions: "raw instructions (must NOT be used directly)",
                ModelProfile: null,
                ReasoningEffort: null,
                AgentDefinitionKind.Single,
                AllowedToolNames: [],
                new Dictionary<string, bool>(StringComparer.Ordinal),
                OrchestrationTopologyJson: null,
                Version: 7,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0);

        /// <summary>Reads the harness's mutable queue age, so a test can move the deadline after the lease is requested.</summary>
        private sealed class QueueAgeOptions(Harness harness) : IOptions<IntegrationOptions>
        {
            public IntegrationOptions Value => new()
            {
                MaxQueueAgeSeconds = harness.MaxQueueAgeSeconds
            };
        }

        private sealed class TrackingDisposable(Harness harness) : IDisposable
        {
            public void Dispose() =>
                harness.RecordReservationDisposed();
        }

        private sealed class TrackingAsyncDisposable(Harness harness) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                harness.RecordLeaseDisposed();
                return ValueTask.CompletedTask;
            }
        }
    }
}
