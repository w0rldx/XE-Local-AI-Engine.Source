namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The execution store's whole surface, against a real on-disk SQLite file: the hard-bounded admission transaction,
///     the non-terminal status compare-and-swap, the one crash-atomic terminal transition, the append feed and the two
///     ordered reads.
/// </summary>
public sealed class IntegrationExecutionStoreTests
{
    private static readonly IReadOnlySet<IntegrationExecutionStatus> Running = new HashSet<IntegrationExecutionStatus> { IntegrationExecutionStatus.Running };
    private static readonly IReadOnlySet<IntegrationExecutionStatus> Accepted = new HashSet<IntegrationExecutionStatus> { IntegrationExecutionStatus.Accepted };

    [Test]
    public async Task AcceptAsync_WritesSessionExecutionAndTheAcceptedEventInOneCommittedTransaction()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var command = NewAccept(seed);

        AssertEx.True(await store.AcceptAsync(command, maxActive: 8, maxActivePerPrincipal: 2).ConfigureAwait(false));

        var execution = AssertEx.NotNull(await store.GetByIdAsync(command.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(IntegrationExecutionStatus.Accepted, execution.Status);
        AssertEx.Equal(seed.PrincipalId, execution.PrincipalId);
        AssertEx.Equal(expected: 0, execution.OutputCount);
        AssertEx.Equal(expected: 0L, execution.OutputBytes);
        AssertEx.Equal(expected: 1L, execution.LastSequence);
        AssertEx.Null(execution.StopRequestedAtUtc);

        var session = AssertEx.NotNull(await new IntegrationSessionStore(context).GetByIdAsync(command.SessionId).ConfigureAwait(false));
        AssertEx.Equal(seed.PrincipalId, session.PrincipalId, "A session and its executions always share one principal.");
        AssertEx.Equal(expected: 1, session.ExecutionCount);

        var events = await store.ListEventsAsync(command.ExecutionId, sinceSequence: 0, limit: 10).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count);
        AssertEx.Equal("execution.accepted", events[0].EventType);
        AssertEx.Null(events[0].DetailJson,
            "The accepted event carries no detail — that is the premise that lets the raw-ADO accept path skip encryption entirely.");
    }

    [Test]
    public async Task AcceptAsync_AtTheNodeWideCap_ThrowsAndWritesNothing()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        AssertEx.True(await store.AcceptAsync(NewAccept(seed), maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var before = await CountsAsync(fixture).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<IntegrationQueueFullException>(() => store.AcceptAsync(NewAccept(seed), maxActive: 1, maxActivePerPrincipal: 4))
                          .ConfigureAwait(false);

        // "Reject before acceptance" means exactly this: not one row in any of the three tables.
        AssertEx.Equal(before, await CountsAsync(fixture).ConfigureAwait(false));
    }

    [Test]
    public async Task AcceptAsync_AtThePerPrincipalCap_ThrowsWhileANodeWideSlotIsStillFreeAndAdmitsASecondPrincipal()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        AssertEx.True(await store.AcceptAsync(NewAccept(seed), maxActive: 8, maxActivePerPrincipal: 1).ConfigureAwait(false));

        // The fairness assertion: the node has seven slots left and the principal has none.
        _ = await AssertEx.ThrowsAsync<IntegrationQueueFullException>(() => store.AcceptAsync(NewAccept(seed), maxActive: 8, maxActivePerPrincipal: 1))
                          .ConfigureAwait(false);

        // The other half of the same ruling: a different integrator is unaffected by the first one's saturation.
        var otherPrincipal = Guid.NewGuid();
        AssertEx.True(await store.AcceptAsync(NewAccept(seed with { PrincipalId = otherPrincipal }), maxActive: 8, maxActivePerPrincipal: 1).ConfigureAwait(false));
    }

    [Test]
    public async Task AcceptAsync_WhenTheCredentialWasRevokedInsideTheWindow_ReturnsFalseAndWritesNothing()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        AssertEx.True(await new IntegrationApiKeyStore(context, TimeProvider.System).RevokeAsync(seed.KeyId, atUtc: 5_000).ConfigureAwait(false));

        var before = await CountsAsync(fixture).ConfigureAwait(false);

        // False rather than an exception, because the caller answers the same generic 401 it uses for any other invalid
        // credential — not the 503 the queue-full exception maps to.
        AssertEx.False(await store.AcceptAsync(NewAccept(seed), maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
        AssertEx.Equal(before, await CountsAsync(fixture).ConfigureAwait(false));
    }

    [Test]
    public async Task AcceptAsync_TerminalRowsDoNotCountTowardEitherCap()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var first = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(first, maxActive: 1, maxActivePerPrincipal: 1).ConfigureAwait(false));

        AssertEx.True(await store.TryTerminalizeAsync(new IntegrationTerminalizeCommand(first.ExecutionId,
                          ExpectedVersion: 0,
                          Accepted,
                          IntegrationExecutionStatus.Completed,
                          Sequence: 2,
                          "execution.completed",
                          EndedAtUtc: 9_000,
                          FailureCategory: null,
                          FailureSummary: null))
                      .ConfigureAwait(false));

        AssertEx.True(await store.AcceptAsync(NewAccept(seed), maxActive: 1, maxActivePerPrincipal: 1).ConfigureAwait(false),
            "Only Accepted, Queued and Running occupy a slot.");
    }

    [Test]
    public async Task AcceptAsync_WithNoNewSession_BumpsTheExistingSessionCounters()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var first = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(first, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var continuation = NewAccept(seed) with
        {
            NewSession = null,
            SessionId = first.SessionId,
            ReceivedAtUtc = 7_777
        };
        AssertEx.True(await store.AcceptAsync(continuation, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var session = AssertEx.NotNull(await new IntegrationSessionStore(context).GetByIdAsync(first.SessionId).ConfigureAwait(false));
        AssertEx.Equal(expected: 2, session.ExecutionCount, "The accept transaction is the only writer of these two columns.");
        AssertEx.Equal(expected: 7_777L, session.LastActivityUtc);
    }

    [Test]
    public async Task AcceptAsync_ContinuationOntoASessionItMayNotJoin_ThrowsAndWritesNothing()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var first = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(first, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var missing = NewAccept(seed) with { NewSession = null, SessionId = Guid.NewGuid() };
        var foreign = NewAccept(seed) with { NewSession = null, SessionId = first.SessionId, PrincipalId = Guid.NewGuid() };

        foreach (var refused in new[] { missing, foreign })
        {
            var before = await CountsAsync(fixture).ConfigureAwait(false);
            _ = await AssertEx.ThrowsAsync<IntegrationSessionUnavailableException>(() => store.AcceptAsync(refused, maxActive: 8, maxActivePerPrincipal: 4))
                              .ConfigureAwait(false);
            AssertEx.Equal(before, await CountsAsync(fixture).ConfigureAwait(false),
                "An unscoped UPDATE would have affected no row and still committed the execution.");
        }

        // The same session, its own principal, but closed: no further execution may join it.
        await fixture.RawExecuteAsync("UPDATE integration_sessions SET status = 'Closed' WHERE id = $id;",
                         command => command.Parameters.AddWithValue("$id", first.SessionId))
                     .ConfigureAwait(false);

        var closedBefore = await CountsAsync(fixture).ConfigureAwait(false);
        var closed = NewAccept(seed) with { NewSession = null, SessionId = first.SessionId };
        _ = await AssertEx.ThrowsAsync<IntegrationSessionUnavailableException>(() => store.AcceptAsync(closed, maxActive: 8, maxActivePerPrincipal: 4))
                          .ConfigureAwait(false);
        AssertEx.Equal(closedBefore, await CountsAsync(fixture).ConfigureAwait(false));

        // And the admitted case, so the scoping is not simply refusing everything.
        await fixture.RawExecuteAsync("UPDATE integration_sessions SET status = 'Active' WHERE id = $id;",
                         command => command.Parameters.AddWithValue("$id", first.SessionId))
                     .ConfigureAwait(false);
        AssertEx.True(await store.AcceptAsync(NewAccept(seed) with { NewSession = null, SessionId = first.SessionId }, maxActive: 8, maxActivePerPrincipal: 4)
                                 .ConfigureAwait(false));
    }

    [Test]
    public async Task AcceptAsync_WhenTheCommandDisagreesWithItself_ThrowsAndWritesNothing()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var before = await CountsAsync(fixture).ConfigureAwait(false);

        var wrongSessionId = NewAccept(seed);
        wrongSessionId = wrongSessionId with { NewSession = wrongSessionId.NewSession! with { SessionId = Guid.NewGuid() } };

        var wrongTriggerId = NewAccept(seed);
        wrongTriggerId = wrongTriggerId with { NewSession = wrongTriggerId.NewSession! with { TriggerId = Guid.NewGuid() } };

        var wrongExecutionId = NewAccept(seed);
        wrongExecutionId = wrongExecutionId with { AcceptedEvent = wrongExecutionId.AcceptedEvent with { ExecutionId = Guid.NewGuid() } };

        foreach (var contradictory in new[] { wrongSessionId, wrongTriggerId, wrongExecutionId })
        {
            _ = await AssertEx.ThrowsAsync<ArgumentException>(() => store.AcceptAsync(contradictory, maxActive: 8, maxActivePerPrincipal: 4)).ConfigureAwait(false);
            AssertEx.Equal(before, await CountsAsync(fixture).ConfigureAwait(false),
                "The command carries each identity twice; a caller that disagrees with itself must not commit an unreachable row.");
        }
    }

    [Test]
    public async Task AcceptAsync_UnderConcurrency_CommitsExactlyTheCapAndNoMore()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);
        const int MaxActive = 4;

        // BEGIN IMMEDIATE takes SQLite's write lock at statement one, so a second concurrent accept blocks instead of
        // reading the same count and admitting alongside the first. A deferred transaction — or a plain SaveChanges —
        // over-admits and fails this.
        var attempts = Enumerable.Range(start: 0, MaxActive + 4)
                                 .Select(async _ =>
                                 {
                                     await using var context = fixture.CreateContext();
                                     var store = new IntegrationExecutionStore(context);
                                     try
                                     {
                                         return await store.AcceptAsync(NewAccept(seed), MaxActive, maxActivePerPrincipal: 64).ConfigureAwait(false);
                                     }
                                     catch (IntegrationQueueFullException)
                                     {
                                         return false;
                                     }
                                 })
                                 .ToArray();

        var results = await Task.WhenAll(attempts).ConfigureAwait(false);

        AssertEx.Equal(MaxActive, results.Count(static admitted => admitted));
        AssertEx.Equal((long)MaxActive, await fixture.RawTableCountAsync("integration_executions").ConfigureAwait(false));
    }

    [Test]
    public async Task UpdateStatusAsync_RefusesAStaleVersionAnUnexpectedStatusAndAMissingRow()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var stale = new IntegrationExecutionStatusUpdate(accept.ExecutionId, ExpectedVersion: 99, Accepted, IntegrationExecutionStatus.Running);
        AssertEx.False(await store.UpdateStatusAsync(stale).ConfigureAwait(false));

        var wrongStatus = new IntegrationExecutionStatusUpdate(accept.ExecutionId, ExpectedVersion: 0, Running, IntegrationExecutionStatus.Running);
        AssertEx.False(await store.UpdateStatusAsync(wrongStatus).ConfigureAwait(false));

        var missing = new IntegrationExecutionStatusUpdate(Guid.NewGuid(), ExpectedVersion: 0, Accepted, IntegrationExecutionStatus.Running);
        AssertEx.False(await store.UpdateStatusAsync(missing).ConfigureAwait(false));

        var unchanged = AssertEx.NotNull(await store.GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(IntegrationExecutionStatus.Accepted, unchanged.Status);
        AssertEx.Equal(expected: 0L, unchanged.Version);
    }

    [Test]
    public async Task UpdateStatusAsync_AppliesOnlyTheNonNullFieldsAndLeavesAnExistingSummaryIntact()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var invocationId = Guid.NewGuid();
        AssertEx.True(await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(accept.ExecutionId,
                          ExpectedVersion: 0,
                          Accepted,
                          IntegrationExecutionStatus.Running,
                          StartedAtUtc: 5_500,
                          InvocationId: invocationId,
                          FailureSummary: "a first attempt"))
                      .ConfigureAwait(false));

        // A command carrying no FailureSummary must leave the existing one intact: null means "leave alone", never
        // "clear it".
        AssertEx.True(await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(accept.ExecutionId,
                          ExpectedVersion: 1,
                          Running,
                          IntegrationExecutionStatus.Running,
                          StopRequestedAtUtc: 6_000))
                      .ConfigureAwait(false));

        var row = AssertEx.NotNull(await store.GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(IntegrationExecutionStatus.Running, row.Status, "A { Running } to Running self-move is the cancel path's marker write.");
        AssertEx.Equal(expected: 5_500L, row.StartedAtUtc);
        AssertEx.Equal(invocationId, row.InvocationId);
        AssertEx.Equal(expected: 6_000L, row.StopRequestedAtUtc);
        AssertEx.Equal("a first attempt", row.FailureSummary);
        AssertEx.Equal(expected: 2L, row.Version);
    }

    [Test]
    public async Task UpdateStatusAsync_TwoRacingUpdatesOnOneVersionResolveToExactlyOneWinner()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);
        Guid executionId;

        await using (var context = fixture.CreateContext())
        {
            var accept = NewAccept(seed);
            AssertEx.True(await new IntegrationExecutionStore(context).AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
            executionId = accept.ExecutionId;
        }

        // Two independent contexts, so neither sees the other's change tracker; the concurrency-token mapping is the
        // only thing that can break the tie.
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var command = new IntegrationExecutionStatusUpdate(executionId, ExpectedVersion: 0, Accepted, IntegrationExecutionStatus.Running);

        var outcomes = await Task.WhenAll(new IntegrationExecutionStore(first).UpdateStatusAsync(command),
                                     new IntegrationExecutionStore(second).UpdateStatusAsync(command))
                                 .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcomes.Count(static won => won));
    }

    [Test]
    public async Task TryTerminalizeAsync_RollsTheStatusBackWithTheEventWhenTheEventInsertViolatesItsUniqueIndex()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
        AssertEx.True(await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(accept.ExecutionId,
                          ExpectedVersion: 0,
                          Accepted,
                          IntegrationExecutionStatus.Running))
                      .ConfigureAwait(false));

        // Occupy the sequence the terminal event is about to claim, so the insert violates
        // ux_integration_execution_events_execution_sequence inside the save.
        await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 5, "tool.started", """{"name":"x"}""", OccurredAtUtc: 8_000))
                   .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DbUpdateException>(() => store.TryTerminalizeAsync(new IntegrationTerminalizeCommand(accept.ExecutionId,
                              ExpectedVersion: 1,
                              Running,
                              IntegrationExecutionStatus.Failed,
                              Sequence: 5,
                              "execution.failed",
                              EndedAtUtc: 9_000,
                              "internal-failure",
                              "boom")))
                          .ConfigureAwait(false);

        // One SaveChanges is one transaction, so the status went back with the event. The round-4 split write
        // (UpdateStatusAsync then AppendEventAsync) fails this outright.
        await using var freshContext = fixture.CreateContext();
        var row = AssertEx.NotNull(await new IntegrationExecutionStore(freshContext).GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(IntegrationExecutionStatus.Running, row.Status);
        AssertEx.Equal(expected: 1L, row.Version);
        AssertEx.Null(row.EndedAtUtc);
        AssertEx.Null(row.FailureCategory);
        AssertEx.Equal(expected: 5L, row.LastSequence, "The watermark must not have moved past the event that was never written.");
    }

    [Test]
    public async Task TryTerminalizeAsync_WhenTheCasLoses_WritesNothingInEitherTable()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        var eventsBefore = await fixture.RawTableCountAsync("integration_execution_events").ConfigureAwait(false);

        foreach (var losing in new[]
                 {
                     Terminal(accept.ExecutionId, expectedVersion: 99, Accepted),
                     Terminal(accept.ExecutionId, expectedVersion: 0, Running),
                     Terminal(Guid.NewGuid(), expectedVersion: 0, Accepted)
                 })
        {
            AssertEx.False(await store.TryTerminalizeAsync(losing).ConfigureAwait(false));
            AssertEx.Equal(eventsBefore, await fixture.RawTableCountAsync("integration_execution_events").ConfigureAwait(false),
                "\"Nothing is written\" now covers two tables, not one.");
        }

        var row = AssertEx.NotNull(await store.GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(IntegrationExecutionStatus.Accepted, row.Status);
        AssertEx.Equal(expected: 0L, row.Version);
    }

    [Test]
    public async Task TryTerminalizeAsync_TwoRacingTerminalisationsProduceOneWinnerAndOneTerminalEvent()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);
        Guid executionId;

        await using (var context = fixture.CreateContext())
        {
            var accept = NewAccept(seed);
            AssertEx.True(await new IntegrationExecutionStore(context).AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
            executionId = accept.ExecutionId;
        }

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        // Distinct sequences, because each caller reserved its own; only the CAS may break the tie.
        var outcomes = await Task.WhenAll(SafeTerminalizeAsync(new IntegrationExecutionStore(first), Terminal(executionId, expectedVersion: 0, Accepted, sequence: 2)),
                                     SafeTerminalizeAsync(new IntegrationExecutionStore(second), Terminal(executionId, expectedVersion: 0, Accepted, sequence: 3)))
                                 .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcomes.Count(static won => won), "This is the queued-cancel race, and exactly one side must win it.");
        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("integration_execution_events").ConfigureAwait(false),
            "The accepted event plus exactly one terminal event.");
    }

    [Test]
    public async Task TryTerminalizeAsync_RecoveryPathWritesTheFailureDetailAndBothWatermarks()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
        AssertEx.True(await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(accept.ExecutionId,
                          ExpectedVersion: 0,
                          Accepted,
                          IntegrationExecutionStatus.Running))
                      .ConfigureAwait(false));
        await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 7, "tool.completed", """{"ok":true}""", OccurredAtUtc: 8_000))
                   .ConfigureAwait(false);

        AssertEx.True(await store.TryTerminalizeAsync(new IntegrationTerminalizeCommand(accept.ExecutionId,
                          ExpectedVersion: 1,
                          Running,
                          IntegrationExecutionStatus.Failed,
                          Sequence: 8,
                          "execution.failed",
                          EndedAtUtc: 9_100,
                          "restart",
                          "interrupted by a host restart"))
                      .ConfigureAwait(false));

        await using var readContext = fixture.CreateContext();
        var readStore = new IntegrationExecutionStore(readContext);
        var row = AssertEx.NotNull(await readStore.GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal("restart", row.FailureCategory);
        AssertEx.Equal(expected: 9_100L, row.EndedAtUtc);
        AssertEx.Equal(expected: 2L, row.Version);
        AssertEx.Equal(expected: 8L, row.LastSequence);

        var terminal = (await readStore.ListEventsAsync(accept.ExecutionId, sinceSequence: 7, limit: 10).ConfigureAwait(false)).Single();
        AssertEx.Equal(expected: 8L, terminal.Sequence);
        // Reading the detail back as text is what catches an implementation that wrote it raw and stored plaintext.
        AssertEx.Equal("""{"failureCategory":"restart","failureSummary":"interrupted by a host restart"}""", terminal.DetailJson);

        var session = AssertEx.NotNull(await new IntegrationSessionStore(readContext).GetByIdAsync(accept.SessionId).ConfigureAwait(false));
        AssertEx.Equal(expected: 8L, session.LastSequence,
            "Terminalisation is the second writer of the session watermark, which is how it can bypass AppendEventAsync without stranding it.");
        AssertEx.Equal(expected: 9_100L, session.LastActivityUtc);
    }

    [Test]
    public async Task TryTerminalizeAsync_ACompletedTerminalisationClearsAnEarlierFailureCategory()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
        AssertEx.True(await store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(accept.ExecutionId,
                          ExpectedVersion: 0,
                          Accepted,
                          IntegrationExecutionStatus.Running,
                          FailureCategory: "capacity-rejected"))
                      .ConfigureAwait(false));

        AssertEx.True(await store.TryTerminalizeAsync(new IntegrationTerminalizeCommand(accept.ExecutionId,
                          ExpectedVersion: 1,
                          Running,
                          IntegrationExecutionStatus.Completed,
                          Sequence: 2,
                          "execution.completed",
                          EndedAtUtc: 9_200,
                          FailureCategory: null,
                          FailureSummary: null))
                      .ConfigureAwait(false));

        var row = AssertEx.NotNull(await store.GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Null(row.FailureCategory, "Assigned, not merged: a terminal write is the final word on why a run ended.");

        var terminal = (await store.ListEventsAsync(accept.ExecutionId, sinceSequence: 1, limit: 10).ConfigureAwait(false)).Single();
        AssertEx.Null(terminal.DetailJson);
    }

    [Test]
    public async Task AppendEventAsync_KeepsTheExecutionWatermarkAtTheMaximumAndTheSessionWatermarkAtTheNewest()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 9, "tool.started", null, OccurredAtUtc: 8_000))
                   .ConfigureAwait(false);
        await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 4, "tool.completed", null, OccurredAtUtc: 8_100))
                   .ConfigureAwait(false);

        var row = AssertEx.NotNull(await store.GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(expected: 9L, row.LastSequence, "A plain assignment would let the slower writer move the execution watermark backwards.");

        var session = AssertEx.NotNull(await new IntegrationSessionStore(context).GetByIdAsync(accept.SessionId).ConfigureAwait(false));
        AssertEx.Equal(expected: 4L, session.LastSequence,
            "Sequences restart per execution, so a MAX across a session would freeze at the deepest old stream; this is an activity indicator, not an ordering key.");

        _ = await AssertEx.ThrowsAsync<DbUpdateException>(
                              () => store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 9, "tool.started", null, 8_200)))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task AppendEventAsync_AfterAFailedSave_LeavesTheStoreUsableAndReplaysNothing()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        // Sequence 1 is the accepted event's, so this violates ux_integration_execution_events_execution_sequence.
        _ = await AssertEx.ThrowsAsync<DbUpdateException>(
                              () => store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 1, "tool.started", null, 8_000)))
                          .ConfigureAwait(false);

        // The SAME store instance, i.e. the same scoped context. Without the tracker clear its next save replays the
        // rejected row and throws again.
        await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 2, "tool.completed", null, OccurredAtUtc: 8_100))
                   .ConfigureAwait(false);

        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("integration_execution_events").ConfigureAwait(false),
            "The accepted event plus the one valid append — the duplicate was never written.");
    }

    [Test]
    public async Task AppendEventAsync_BoundsANonOutputEventDetailAtFourKibibytes()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 2, "tool.completed", new string('a', count: 4096), 8_000))
                   .ConfigureAwait(false);

        var before = await fixture.RawTableCountAsync("integration_execution_events").ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<ArgumentException>(
                              () => store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(),
                                  accept.ExecutionId,
                                  Sequence: 3,
                                  "tool.completed",
                                  new string('a', count: 4097),
                                  OccurredAtUtc: 8_100)))
                          .ConfigureAwait(false);
        AssertEx.Equal(before, await fixture.RawTableCountAsync("integration_execution_events").ConfigureAwait(false));

        // external.output is exempt: its payload is the caller-facing one, bounded at MaxOutputBytes by the append
        // path S3 adds for it.
        await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(), accept.ExecutionId, Sequence: 4, "external.output", new string('a', count: 4097), 8_200))
                   .ConfigureAwait(false);
    }

    [Test]
    public async Task ListEventsAsync_ReturnsDecryptedTextAndRefusesANonPositiveLimit()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);
        Guid executionId;

        await using (var context = fixture.CreateContext())
        {
            var store = new IntegrationExecutionStore(context);
            var accept = NewAccept(seed);
            AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
            await store.AppendEventAsync(new IntegrationEventAppend(Guid.NewGuid(),
                           accept.ExecutionId,
                           Sequence: 2,
                           "external.output",
                           """{"reading":42}""",
                           OccurredAtUtc: 8_000))
                       .ConfigureAwait(false);
            executionId = accept.ExecutionId;
        }

        // A fresh context, so the answer comes off the file through the decrypt path. A LINQ projection would return
        // ciphertext here and this is the assertion that catches it.
        await using var readContext = fixture.CreateContext();
        var readStore = new IntegrationExecutionStore(readContext);
        var events = await readStore.ListEventsAsync(executionId, sinceSequence: 1, limit: 10).ConfigureAwait(false);
        AssertEx.Equal("""{"reading":42}""", events.Single().DetailJson);

        _ = await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() => readStore.ListEventsAsync(executionId, sinceSequence: 0, limit: 0)).ConfigureAwait(false);
    }

    [Test]
    public async Task ListAsync_OrdersNewestFirstWithAnIdTieBreakAndPagesDeterministically()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);

        // Two rows sharing one millisecond stamp: without the Id tie-break these page non-deterministically.
        var older = NewAccept(seed) with { ReceivedAtUtc = 1_000 };
        var tieA = NewAccept(seed) with { ReceivedAtUtc = 2_000 };
        var tieB = NewAccept(seed) with { ReceivedAtUtc = 2_000 };
        foreach (var accept in new[] { older, tieA, tieB })
        {
            AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 8).ConfigureAwait(false));
        }

        var all = await store.ListAsync(new IntegrationExecutionFilter(TriggerId: null, SessionId: null, Status: null, Limit: 10, Offset: 0)).ConfigureAwait(false);
        AssertEx.Equal(expected: 3, all.Count);
        AssertEx.Equal(older.ExecutionId, all[^1].Id, "Ordered ReceivedAtUtc descending.");

        var page0 = await store.ListAsync(new IntegrationExecutionFilter(null, null, null, Limit: 1, Offset: 0)).ConfigureAwait(false);
        var page1 = await store.ListAsync(new IntegrationExecutionFilter(null, null, null, Limit: 1, Offset: 1)).ConfigureAwait(false);
        AssertEx.False(page0.Single().Id == page1.Single().Id, "No row may be dropped or repeated across pages.");
        AssertEx.Equal(all[0].Id, page0.Single().Id);
        AssertEx.Equal(all[1].Id, page1.Single().Id);

        var byStatus = await store.ListAsync(new IntegrationExecutionFilter(null, null, IntegrationExecutionStatus.Running, Limit: 10, Offset: 0)).ConfigureAwait(false);
        AssertEx.Empty(byStatus);

        var bySession = await store.ListAsync(new IntegrationExecutionFilter(null, tieA.SessionId, null, Limit: 10, Offset: 0)).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, bySession.Count);
    }

    [Test]
    public async Task GetByRequestIdAsync_IsScopedToItsOwnPrincipal()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        var accept = NewAccept(seed);
        AssertEx.True(await store.AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));

        AssertEx.NotNull(await store.GetByRequestIdAsync(seed.PrincipalId, accept.RequestId).ConfigureAwait(false));
        AssertEx.Null(await store.GetByRequestIdAsync(Guid.NewGuid(), accept.RequestId).ConfigureAwait(false),
            "A replay must only ever see its own principal's rows; otherwise one integrator's 409 is another's request id.");
    }

    [Test]
    public async Task CountActiveBySessionAsync_CountsOnlyOneSessionsNonTerminalRows()
    {
        // Not the node-wide count the admission transaction folds in: this is the per-SESSION busy read behind the 409
        // that a second concurrent invoke and an operator delete both need.
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var mine = IntegrationTestFixture.Session(seed.TriggerId, seed.PrincipalId);
        var other = IntegrationTestFixture.Session(seed.TriggerId, seed.PrincipalId);
        context.IntegrationSessions.AddRange(mine, other);
        context.IntegrationExecutions.AddRange(
            IntegrationTestFixture.Execution(seed.TriggerId, mine.Id, seed.PrincipalId, status: IntegrationExecutionStatus.Accepted),
            IntegrationTestFixture.Execution(seed.TriggerId, mine.Id, seed.PrincipalId, status: IntegrationExecutionStatus.Queued),
            IntegrationTestFixture.Execution(seed.TriggerId, mine.Id, seed.PrincipalId, status: IntegrationExecutionStatus.Running),
            IntegrationTestFixture.Execution(seed.TriggerId, mine.Id, seed.PrincipalId, status: IntegrationExecutionStatus.Completed),
            IntegrationTestFixture.Execution(seed.TriggerId, mine.Id, seed.PrincipalId, status: IntegrationExecutionStatus.Failed),
            IntegrationTestFixture.Execution(seed.TriggerId, mine.Id, seed.PrincipalId, status: IntegrationExecutionStatus.Cancelled),
            IntegrationTestFixture.Execution(seed.TriggerId, other.Id, seed.PrincipalId, status: IntegrationExecutionStatus.Running));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);

        var store = new IntegrationExecutionStore(context);
        AssertEx.Equal(expected: 3, await store.CountActiveBySessionAsync(mine.Id).ConfigureAwait(false));
        AssertEx.Equal(expected: 1, await store.CountActiveBySessionAsync(other.Id).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await store.CountActiveBySessionAsync(Guid.NewGuid()).ConfigureAwait(false));
    }

    [Test]
    public async Task AppendEventAsync_WhenTwoContextsRaceOnOneExecution_KeepsTheHIGHESTWatermark()
    {
        // The lost update this closes: both writers loaded LastSequence, applied Math.Max in memory and saved through
        // separate contexts, so whichever committed last wrote its own stale number. Recovery then seeded the replay
        // ring below a sequence that already had a row, and every restart re-collided on the unique index.
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);
        var accept = await AcceptOneAsync(fixture, seed).ConfigureAwait(false);

        await using var slow = fixture.CreateContext();
        await using var fast = fixture.CreateContext();
        var slowStore = new IntegrationExecutionStore(slow);
        var fastStore = new IntegrationExecutionStore(fast);

        // The slow writer already holds the row in its change tracker at LastSequence 1 — exactly the state the append
        // path used to read from, because EF's identity map returns the TRACKED instance rather than the fresh row.
        var tracked = await slow.IntegrationExecutions.SingleAsync(row => row.Id == accept.ExecutionId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1L, tracked.LastSequence);

        await fastStore.AppendEventAsync(Append(accept.ExecutionId, sequence: 4, "external.output")).ConfigureAwait(false);
        await slowStore.AppendEventAsync(Append(accept.ExecutionId, sequence: 3, "tool.completed")).ConfigureAwait(false);

        await using var reader = fixture.CreateContext();
        var row = AssertEx.NotNull(await new IntegrationExecutionStore(reader).GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(expected: 4L, row.LastSequence, "The slower writer's stale 3 must never move the watermark back below the committed 4.");
    }

    [Test]
    public async Task TryTerminalizeAsync_WritesTheAuditRowInTheSAMETransactionAsTheTerminal()
    {
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);
        var accept = await AcceptOneAsync(fixture, seed).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);
        AssertEx.True(await store.TryTerminalizeAsync(Terminal(accept.ExecutionId, expectedVersion: 0, Accepted) with
        {
            Audit = Audit(seed)
        }).ConfigureAwait(false));

        AssertEx.Equal(expected: 1L, await fixture.RawScalarAsync("SELECT COUNT(*) FROM agent_execution_logs WHERE record_kind = 3;").ConfigureAwait(false));
    }

    [Test]
    public async Task TryTerminalizeAsync_WhenTheTerminalEventCannotBeWritten_RollsBackTheAuditRowToo()
    {
        // The failure Codex named: the audit insert used to be a SEPARATE SaveChanges after the terminal committed, so
        // a storage failure between the two lost the one-per-execution audit row forever — every later terminalization
        // rejects an already-terminal row. Driven here from the other side: a terminal event that violates the unique
        // sequence index must take the audit row down with it, and leave the row non-terminal for a retry.
        using var fixture = new IntegrationTestFixture();
        var seed = await SeedAsync(fixture).ConfigureAwait(false);
        var accept = await AcceptOneAsync(fixture, seed).ConfigureAwait(false);

        await using var context = fixture.CreateContext();
        var store = new IntegrationExecutionStore(context);

        // Sequence 1 is the accepted event's, already committed by the admission transaction.
        _ = await AssertEx.ThrowsAsync<DbUpdateException>(() => store.TryTerminalizeAsync(Terminal(accept.ExecutionId, expectedVersion: 0, Accepted, sequence: 1) with
                          {
                              Audit = Audit(seed)
                          }))
                          .ConfigureAwait(false);

        AssertEx.Equal(expected: 0L, await fixture.RawScalarAsync("SELECT COUNT(*) FROM agent_execution_logs WHERE record_kind = 3;").ConfigureAwait(false),
            "A rolled-back terminal must leave no audit row behind, or the row would claim an outcome the execution never reached.");

        await using var reader = fixture.CreateContext();
        var row = AssertEx.NotNull(await new IntegrationExecutionStore(reader).GetByIdAsync(accept.ExecutionId).ConfigureAwait(false));
        AssertEx.Equal(IntegrationExecutionStatus.Accepted, row.Status, "Nothing about the failed terminal may survive it.");
        AssertEx.Equal(expected: 1L, row.LastSequence);
    }

    private static IntegrationInvocationAuditInput Audit(SeedState seed) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            "sensor-ingest",
            seed.KeyPrefix,
            seed.AgentDefinitionId,
            "cancelled",
            TraceId: null,
            LatencyMs: 12);

    private static IntegrationEventAppend Append(Guid executionId, long sequence, string eventType) =>
        new(Guid.NewGuid(), executionId, sequence, eventType, """{"ok":true}""", OccurredAtUtc: 5_000);

    /// <summary>One admitted execution through the real accept transaction, so its row and its sequence 1 are real.</summary>
    private static async Task<IntegrationAcceptCommand> AcceptOneAsync(IntegrationTestFixture fixture, SeedState seed)
    {
        await using var context = fixture.CreateContext();
        var accept = NewAccept(seed);
        AssertEx.True(await new IntegrationExecutionStore(context).AcceptAsync(accept, maxActive: 8, maxActivePerPrincipal: 4).ConfigureAwait(false));
        return accept;
    }

    private static IntegrationTerminalizeCommand Terminal(Guid executionId,
        long expectedVersion,
        IReadOnlySet<IntegrationExecutionStatus> expectedStatuses,
        long sequence = 2) =>
        new(executionId,
            expectedVersion,
            expectedStatuses,
            IntegrationExecutionStatus.Cancelled,
            sequence,
            "execution.cancelled",
            EndedAtUtc: 9_000,
            FailureCategory: null,
            FailureSummary: null);

    private static async Task<bool> SafeTerminalizeAsync(IIntegrationExecutionStore store, IntegrationTerminalizeCommand command)
    {
        try
        {
            return await store.TryTerminalizeAsync(command).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A losing writer may surface its lost CAS as a unique-index violation on the row it raced for; either way
            // it did not win.
            return false;
        }
    }

    private static IntegrationAcceptCommand NewAccept(SeedState seed)
    {
        var executionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        return new IntegrationAcceptCommand(new IntegrationSessionCreate(sessionId, seed.TriggerId, Guid.NewGuid(), seed.AgentDefinitionId),
            executionId,
            seed.TriggerId,
            sessionId,
            seed.PrincipalId,
            Guid.NewGuid(),
            new byte[32],
            seed.KeyPrefix,
            ReceivedAtUtc: 3_000,
            new IntegrationEventAppend(Guid.NewGuid(), executionId, Sequence: 1, "execution.accepted", DetailJson: null, OccurredAtUtc: 3_000));
    }

    private static async Task<SeedState> SeedAsync(IntegrationTestFixture fixture)
    {
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var trigger = IntegrationTestFixture.Trigger();
        var key = IntegrationTestFixture.ApiKey();
        _ = context.IntegrationTriggers.Add(trigger);
        _ = context.IntegrationApiKeys.Add(key);
        _ = await context.SaveChangesAsync().ConfigureAwait(false);

        return new SeedState(trigger.Id, trigger.TargetAgentDefinitionId, key.Id, key.PrincipalId, key.KeyPrefix);
    }

    private static async Task<(long Sessions, long Executions, long Events)> CountsAsync(IntegrationTestFixture fixture) =>
        (await fixture.RawTableCountAsync("integration_sessions").ConfigureAwait(false),
            await fixture.RawTableCountAsync("integration_executions").ConfigureAwait(false),
            await fixture.RawTableCountAsync("integration_execution_events").ConfigureAwait(false));

    private sealed record SeedState(Guid TriggerId, Guid AgentDefinitionId, Guid KeyId, Guid PrincipalId, string KeyPrefix);
}
