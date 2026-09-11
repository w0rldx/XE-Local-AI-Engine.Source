namespace XE_Local_AI_Engine.Client.Persistence.Tests.ExternalApps;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The store's compare-and-swap contract: what a winning write leaves behind, and that a losing one leaves nothing
///     at all — not even its event, which is the whole reason the sequence is minted inside the transaction.
/// </summary>
public sealed class ExternalAppInstanceStoreTests
{
    [Test]
    public async Task CreateAsync_WritesTheRowAndItsFirstEventAtSequenceOne()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();

        var result = await store.CreateAsync(command).ConfigureAwait(false);

        AssertEx.True(result.Applied);
        AssertEx.Equal(expected: 1L, result.Sequence, "The first event is sequence 1; nothing reserves sequence 0.");
        AssertEx.Equal(expected: 0L, result.Version, "Version starts at 0 — the number the install hands the runner as its first expectedVersion.");

        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, snapshot.Status, "An install row is born Installing, which is what makes it answer 409 to everything else.");
        AssertEx.Equal(ExternalAppDesiredState.Stopped, snapshot.DesiredState, "Nothing is running yet, so nothing may be restarted by the daemon yet.");
        AssertEx.Equal(ExternalAppTestFixture.SeedVariablesJson, snapshot.VariablesJson, "Variables cross the store boundary as decrypted text in both directions.");
        AssertEx.Equal("{}", snapshot.PublishedPortsJson, "No port is bound until the plan is executed, and the column is not nullable.");
        AssertEx.Equal(expected: 1L, snapshot.LastSequence);

        var events = await store.ListEventsAsync(command.Id, afterSequence: 0, limit: 10).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count);
        AssertEx.Equal(ExternalAppInstanceEventKind.PermissionAccepted, events[0].Kind,
            "The install's first event records the accepted permissions, and it is the caller that names the kind.");
        AssertEx.Equal(expected: 1L, events[0].Sequence);
    }

    [Test]
    public async Task ListByApplicationAsync_ReturnsOnlyThatApplicationsInstances()
    {
        // The install gate's only read: a Where that leaked another application's rows would refuse every install as
        // already-installed, and one that leaked none would let a second instance of the same application through.
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var odysseus = ExternalAppTestFixture.Create(applicationId: "odysseus");
        var other = ExternalAppTestFixture.Create(applicationId: "searxng");
        _ = await store.CreateAsync(odysseus).ConfigureAwait(false);
        _ = await store.CreateAsync(other).ConfigureAwait(false);

        var matches = await store.ListByApplicationAsync("odysseus").ConfigureAwait(false);

        AssertEx.Equal(expected: 1, matches.Count);
        AssertEx.Equal(odysseus.Id, matches[0].Id);
        AssertEx.Empty(await store.ListByApplicationAsync("nothing-installed").ConfigureAwait(false));
        AssertEx.Equal(expected: 2, (await store.ListAsync().ConfigureAwait(false)).Count, "and the unfiltered list still sees both.");
    }

    [Test]
    public async Task UpdateStatusAsync_WhenVersionIsStale_WritesNothingIncludingTheEvent()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        _ = await store.CreateAsync(command).ConfigureAwait(false);

        var result = await store.UpdateStatusAsync(ExternalAppTestFixture.Transition(command.Id,
                                        expectedVersion: 7,
                                        ExternalAppInstanceStatus.Running,
                                        ExternalAppInstanceStatus.Installing))
                                .ConfigureAwait(false);

        AssertEx.False(result.Applied, "A stale version loses the compare-and-swap.");
        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, snapshot.Status);
        AssertEx.Equal(expected: 0L, snapshot.Version);
        AssertEx.Equal(expected: 1L, snapshot.LastSequence, "A lost CAS mints no sequence, which is why the feed has no holes.");
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("external_app_instance_events").ConfigureAwait(false),
            "The event must not be written either: it is inside the same transaction as the status.");
    }

    /// <summary>
    ///     The tracked mutations belong INSIDE the boundary that clears the tracker. Acquiring the transaction after
    ///     them left a failed acquisition with the entity mutated and the event queued on a scoped context, where the
    ///     next save — a different caller's, on the same request — would have committed this transition silently.
    /// </summary>
    [Test]
    public async Task UpdateStatusAsync_WhenTheTransactionCannotBeStarted_LeavesNothingPendingOnTheContext()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        _ = await store.CreateAsync(command).ConfigureAwait(false);

        // A transaction is already in flight on this context, so the store's own cannot be started. A closed
        // connection would fail the read instead and prove nothing about the write it never reached.
        await using var occupied = await context.Database.BeginTransactionAsync().ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => store.UpdateStatusAsync(ExternalAppTestFixture.Transition(command.Id,
                expectedVersion: 0,
                ExternalAppInstanceStatus.Running,
                ExternalAppInstanceStatus.Installing)))
            .ConfigureAwait(false);

        // Not "no entries": the create this test seeded with leaves its own rows tracked, and a saved entity is
        // Unchanged. What must not be here is a PENDING change — the mutated row and the queued event.
        AssertEx.Empty(context.ChangeTracker.Entries().Where(static entry => entry.State != EntityState.Unchanged),
            "A transition that could not even start its transaction must leave no pending change behind.");
    }

    /// <summary>
    ///     The SAME lost compare-and-swap, reported by the other half of the batch. This store MINTS the sequence, so
    ///     a loser's event collides on the instance/sequence index and SQLite raises that before EF gets to count the
    ///     rows its UPDATE did not affect. Without the catch it is an unhandled DbUpdateException, which the API layer
    ///     has no mapping for and answers 500.
    /// </summary>
    [Test]
    public async Task UpdateStatusAsync_WhenTheMintedSequenceIsAlreadyTaken_LosesTheSwapRatherThanThrowing()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        _ = await store.CreateAsync(command).ConfigureAwait(false);

        // The winner's event, written straight to the file at the sequence this store is about to mint. Only another
        // writer that won the swap this one is about to lose can produce it.
        // instance_id is copied from the row itself rather than re-encoded here, so the collision is exact whatever
        // shape the provider stores a Guid in.
        await fixture.RawExecuteAsync(
                         "INSERT INTO external_app_instance_events (id, instance_id, sequence, kind, detail_json, occurred_at_utc) "
                         + "SELECT $id, id, 2, 'Started', NULL, 2000 FROM external_app_instances;",
                         raw => raw.Parameters.AddWithValue("$id", Guid.NewGuid().ToString()))
                     .ConfigureAwait(false);

        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("external_app_instance_events").ConfigureAwait(false),
            "The winner's event must actually be in the file, or nothing collides and this test proves nothing.");

        var result = await store.UpdateStatusAsync(ExternalAppTestFixture.Transition(command.Id,
                                        expectedVersion: 0,
                                        ExternalAppInstanceStatus.Running,
                                        ExternalAppInstanceStatus.Installing))
                                .ConfigureAwait(false);

        AssertEx.False(result.Applied, "A sequence another writer already took means this writer lost; it is not a fault to raise.");

        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, snapshot.Status, "The whole transaction rolls back, status included.");
        AssertEx.Equal(expected: 0L, snapshot.Version);
        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("external_app_instance_events").ConfigureAwait(false),
            "The create's event and the winner's, and nothing this loser wrote.");
    }

    [Test]
    public async Task UpdateStatusAsync_WhenStatusIsOutsideTheExpectedSet_WritesNothing()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        _ = await store.CreateAsync(command).ConfigureAwait(false);

        // The version is current; only the expected status is wrong — a Stop admitted against a row that is still
        // installing, which the transition table refuses.
        var result = await store.UpdateStatusAsync(ExternalAppTestFixture.Transition(command.Id,
                                        expectedVersion: 0,
                                        ExternalAppInstanceStatus.Stopped,
                                        ExternalAppInstanceStatus.Running,
                                        ExternalAppInstanceEventKind.Stopped))
                                .ConfigureAwait(false);

        AssertEx.False(result.Applied);
        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, snapshot.Status);
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("external_app_instance_events").ConfigureAwait(false));
    }

    [Test]
    public async Task UpdateStatusAsync_MintsMonotonicSequencesAcrossManyTransitions()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);

        var version = created.Version;
        var status = ExternalAppInstanceStatus.Installing;
        var expectedSequence = created.Sequence;

        foreach (var (next, kind) in new[]
                 {
                     (ExternalAppInstanceStatus.Running, ExternalAppInstanceEventKind.Installed),
                     (ExternalAppInstanceStatus.Stopping, ExternalAppInstanceEventKind.StopRequested),
                     (ExternalAppInstanceStatus.Stopped, ExternalAppInstanceEventKind.Stopped),
                     (ExternalAppInstanceStatus.Starting, ExternalAppInstanceEventKind.StartRequested),
                     (ExternalAppInstanceStatus.Running, ExternalAppInstanceEventKind.Started)
                 })
        {
            var result = await store.UpdateStatusAsync(ExternalAppTestFixture.Transition(command.Id, version, next, status, kind)).ConfigureAwait(false);

            AssertEx.True(result.Applied, $"The {status} → {next} transition must be admitted.");
            AssertEx.Equal(++expectedSequence, result.Sequence, "Sequences are minted one per status change, with no holes and no reservations.");
            AssertEx.Equal(version + 1, result.Version, "and every transition contends on the next version.");
            version = result.Version;
            status = next;
        }

        var events = await store.ListEventsAsync(command.Id, afterSequence: 0, limit: 100).ConfigureAwait(false);
        AssertEx.Equal(expected: 6, events.Count);
        AssertEx.True(events.Select(row => row.Sequence).SequenceEqual([1L, 2L, 3L, 4L, 5L, 6L]), "The feed is ordered by sequence and complete.");
    }

    [Test]
    public async Task UpdateStatusAsync_WhenTwoWritersRaceOnOneRow_ExactlyOneWins()
    {
        using var fixture = new ExternalAppTestFixture();
        Guid instanceId;

        await using (var seedContext = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var command = ExternalAppTestFixture.Create();
            _ = await new ExternalAppInstanceStore(seedContext).CreateAsync(command).ConfigureAwait(false);
            instanceId = command.Id;
        }

        await using var slow = fixture.CreateContext();
        await using var fast = fixture.CreateContext();
        var slowStore = new ExternalAppInstanceStore(slow);
        var fastStore = new ExternalAppInstanceStore(fast);

        // The slow writer already holds the row in its change tracker at version 0 — exactly the state the pre-check
        // reads, because EF's identity map returns the TRACKED instance rather than the fresh row. Without the
        // concurrency token, both writers would pass that check and the second would overwrite the first's verdict.
        var tracked = await slow.ExternalAppInstances.SingleAsync(row => row.Id == instanceId).ConfigureAwait(false);
        AssertEx.Equal(expected: 0L, tracked.Version);

        var winner = await fastStore.UpdateStatusAsync(ExternalAppTestFixture.Transition(instanceId,
                                         expectedVersion: 0,
                                         ExternalAppInstanceStatus.Running,
                                         ExternalAppInstanceStatus.Installing,
                                         ExternalAppInstanceEventKind.Installed))
                                    .ConfigureAwait(false);
        var loser = await slowStore.UpdateStatusAsync(ExternalAppTestFixture.Transition(instanceId,
                                        expectedVersion: 0,
                                        ExternalAppInstanceStatus.Failed,
                                        ExternalAppInstanceStatus.Installing,
                                        ExternalAppInstanceEventKind.Failed))
                                   .ConfigureAwait(false);

        AssertEx.True(winner.Applied);
        AssertEx.False(loser.Applied, "A lost CAS is a false, never an exception: the service decides whether that means 409 or 'ignore me'.");

        await using var reader = fixture.CreateContext();
        var snapshot = AssertEx.NotNull(await new ExternalAppInstanceStore(reader).GetAsync(instanceId).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Running, snapshot.Status, "The winner's verdict stands.");
        AssertEx.Equal(expected: 1L, snapshot.Version);
        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("external_app_instance_events").ConfigureAwait(false),
            "Two events would mean the loser wrote one anyway: the create's and the winner's are all there may be.");
    }

    [Test]
    public async Task UpdateStatusAsync_NullOptionalFields_LeaveTheStoredValuesAlone()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);

        var seeded = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(command.Id,
                                        created.Version,
                                        new HashSet<ExternalAppInstanceStatus>
                                        {
                                            ExternalAppInstanceStatus.Installing
                                        },
                                        ExternalAppInstanceStatus.Running,
                                        ExternalAppInstanceEventKind.Installed,
                                        EventDetailJson: null,
                                        OccurredAtUtc: 2_000,
                                        ExternalAppDesiredState.Running,
                                        PublishedPortsJson: """{"web":{"7000":41237}}""",
                                        ManifestSnapshotJson: null,
                                        ManifestVersion: null,
                                        RuntimeProvider: "docker-rootless",
                                        StartedAtUtc: 2_000))
                                .ConfigureAwait(false);
        AssertEx.True(seeded.Applied);

        // Everything optional is null this time: a stop that only moves the status must not blank the ports the start
        // wrote, nor the provider, nor the started stamp.
        var second = await store.UpdateStatusAsync(ExternalAppTestFixture.Transition(command.Id,
                                     seeded.Version,
                                     ExternalAppInstanceStatus.Stopping,
                                     ExternalAppInstanceStatus.Running,
                                     ExternalAppInstanceEventKind.StopRequested,
                                     occurredAtUtc: 3_000))
                                .ConfigureAwait(false);
        AssertEx.True(second.Applied);

        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal("""{"web":{"7000":41237}}""", snapshot.PublishedPortsJson);
        AssertEx.Equal("docker-rootless", snapshot.RuntimeProvider);
        AssertEx.Equal(ExternalAppDesiredState.Running, snapshot.DesiredState, "A stop that has not finished must not yet flip what the user asked for.");
        AssertEx.Equal((long?)2_000L, snapshot.StartedAtUtc);
        AssertEx.Null(snapshot.StoppedAtUtc, "and a field nobody has ever set stays null.");
        AssertEx.Equal(ExternalAppTestFixture.SeedManifestJson, snapshot.ManifestSnapshotJson);
    }

    [Test]
    public async Task UpdateStatusAsync_WithClearFailure_ErasesAStaleCategoryAndSummary()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);

        var failed = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(command.Id,
                                        created.Version,
                                        new HashSet<ExternalAppInstanceStatus>
                                        {
                                            ExternalAppInstanceStatus.Installing
                                        },
                                        ExternalAppInstanceStatus.Failed,
                                        ExternalAppInstanceEventKind.Failed,
                                        EventDetailJson: null,
                                        OccurredAtUtc: 2_000,
                                        FailureCategory: ExternalAppFailureCategory.ImagePullFailed,
                                        FailureSummary: "The image could not be pulled at its pinned digest."))
                                .ConfigureAwait(false);
        AssertEx.True(failed.Applied);

        var afterFailure = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal((ExternalAppFailureCategory?)ExternalAppFailureCategory.ImagePullFailed, afterFailure.FailureCategory);

        var recovered = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(command.Id,
                                        failed.Version,
                                        new HashSet<ExternalAppInstanceStatus>
                                        {
                                            ExternalAppInstanceStatus.Failed
                                        },
                                        ExternalAppInstanceStatus.Starting,
                                        ExternalAppInstanceEventKind.StartRequested,
                                        EventDetailJson: null,
                                        OccurredAtUtc: 3_000,
                                        ClearFailure: true))
                                .ConfigureAwait(false);
        AssertEx.True(recovered.Applied);

        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Null(snapshot.FailureCategory, "A retry that no longer fails must not keep showing the reason the previous attempt did.");
        AssertEx.Null(snapshot.FailureSummary);
    }

    [Test]
    public async Task UpdateVariablesAsync_SetsNeedsRecreate()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);

        var applied = await store.UpdateVariablesAsync(command.Id, created.Version, """{"ODYSSEUS_ADMIN_PASSWORD":"rotated"}""", updatedAtUtc: 4_000)
                                 .ConfigureAwait(false);

        AssertEx.True(applied);
        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal("""{"ODYSSEUS_ADMIN_PASSWORD":"rotated"}""", snapshot.VariablesJson);
        AssertEx.True(snapshot.NeedsRecreate, "A created container's environment is immutable, so a configure owes a rebuild.");
        AssertEx.Equal(created.Version + 1, snapshot.Version);
        AssertEx.Equal(expected: 1L, snapshot.LastSequence, "A configure writes no event; the history records lifecycle, not edits.");
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, snapshot.Status, "and it does not move the status.");
    }

    [Test]
    public async Task UpdateVariablesAsync_UnderAStaleVersion_DoesNotOverwrite()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        _ = await store.CreateAsync(command).ConfigureAwait(false);

        var applied = await store.UpdateVariablesAsync(command.Id, expectedVersion: 9, """{"ODYSSEUS_ADMIN_PASSWORD":"stale-writer"}""", updatedAtUtc: 4_000)
                                 .ConfigureAwait(false);

        AssertEx.False(applied);
        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppTestFixture.SeedVariablesJson, snapshot.VariablesJson, "The stale writer's values must not reach the row.");
        AssertEx.False(snapshot.NeedsRecreate);
        AssertEx.Equal(expected: 0L, snapshot.Version);
    }

    [Test]
    public async Task UpdateStatusAsync_CanClearNeedsRecreate()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);
        var configured = await store.UpdateVariablesAsync(command.Id, created.Version, """{"ODYSSEUS_ADMIN_PASSWORD":"rotated"}""", updatedAtUtc: 4_000)
                                    .ConfigureAwait(false);
        AssertEx.True(configured);
        var version = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false)).Version;

        // The start that rebuilt the containers against the new environment is what pays the debt off.
        var started = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(command.Id,
                                       version,
                                       new HashSet<ExternalAppInstanceStatus>
                                       {
                                           ExternalAppInstanceStatus.Installing
                                       },
                                       ExternalAppInstanceStatus.Running,
                                       ExternalAppInstanceEventKind.Started,
                                       EventDetailJson: null,
                                       OccurredAtUtc: 5_000,
                                       NeedsRecreate: false))
                                 .ConfigureAwait(false);

        AssertEx.True(started.Applied);
        AssertEx.False(AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false)).NeedsRecreate);
    }

    [Test]
    public async Task CommitUpdateAsync_WritesTheSnapshotVariablesPortsAndManifestVersionTogetherAndBumpsVersion()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);

        const string TargetManifest = """{"applicationId":"odysseus","manifestVersion":2}""";
        const string TargetVariables = """{"ODYSSEUS_ADMIN_PASSWORD":"correct-horse-battery-staple","NEW_VAR":"x"}""";
        const string TargetPorts = """{"web":{"7000":41999}}""";

        var result = await store.CommitUpdateAsync(command.Id, created.Version, TargetManifest, TargetVariables, TargetPorts, manifestVersion: 2, updatedAtUtc: 6_000)
                                .ConfigureAwait(false);

        AssertEx.True(result.Applied);
        AssertEx.Equal(created.Version + 1, result.Version);

        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(TargetManifest, snapshot.ManifestSnapshotJson);
        AssertEx.Equal(TargetVariables, snapshot.VariablesJson);
        AssertEx.Equal(TargetPorts, snapshot.PublishedPortsJson);
        AssertEx.Equal(expected: 2, snapshot.ManifestVersion);
        AssertEx.Equal(expected: 6_000L, snapshot.UpdatedAtUtc);
    }

    [Test]
    public async Task CommitUpdateAsync_UnderAStaleVersion_WritesNothing()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        _ = await store.CreateAsync(command).ConfigureAwait(false);

        var result = await store.CommitUpdateAsync(command.Id,
                                    expectedVersion: 11,
                                    """{"applicationId":"odysseus","manifestVersion":2}""",
                                    """{"ODYSSEUS_ADMIN_PASSWORD":"stale"}""",
                                    """{"web":{"7000":41999}}""",
                                    manifestVersion: 2,
                                    updatedAtUtc: 6_000)
                                .ConfigureAwait(false);

        AssertEx.False(result.Applied, "A lost commit aborts the update BEFORE any replacement container is started.");
        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppTestFixture.SeedManifestJson, snapshot.ManifestSnapshotJson, "Half the update reaching the row is the failure this method exists to prevent.");
        AssertEx.Equal(ExternalAppTestFixture.SeedVariablesJson, snapshot.VariablesJson);
        AssertEx.Equal("{}", snapshot.PublishedPortsJson);
        AssertEx.Equal(expected: 1, snapshot.ManifestVersion);
        AssertEx.Equal(expected: 0L, snapshot.Version);
    }

    [Test]
    public async Task CommitUpdateAsync_WritesNoEventAndLeavesStatusAlone()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);
        var updating = await store.UpdateStatusAsync(ExternalAppTestFixture.Transition(command.Id,
                                       created.Version,
                                       ExternalAppInstanceStatus.Updating,
                                       ExternalAppInstanceStatus.Installing,
                                       ExternalAppInstanceEventKind.UpdateRequested))
                                  .ConfigureAwait(false);
        AssertEx.True(updating.Applied);

        var result = await store.CommitUpdateAsync(command.Id,
                                    updating.Version,
                                    """{"applicationId":"odysseus","manifestVersion":2}""",
                                    ExternalAppTestFixture.SeedVariablesJson,
                                    """{"web":{"7000":41999}}""",
                                    manifestVersion: 2,
                                    updatedAtUtc: 6_000)
                                .ConfigureAwait(false);

        AssertEx.True(result.Applied);
        AssertEx.Equal(updating.Sequence, result.Sequence, "The commit mints no sequence; it reports the watermark as it stands.");

        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Updating, snapshot.Status, "The update is still running: the commit is a recovery boundary, not a transition.");
        AssertEx.Equal(ExternalAppDesiredState.Stopped, snapshot.DesiredState);
        AssertEx.Equal(expected: 2L, snapshot.LastSequence);
        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("external_app_instance_events").ConfigureAwait(false),
            "The create's event and the UpdateRequested event, and nothing from the commit.");
    }

    [Test]
    public async Task DeleteAsync_RemovesTheEventsAsWellAsTheRow()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var doomed = ExternalAppTestFixture.Create();
        var survivor = ExternalAppTestFixture.Create(applicationId: "searxng");
        var created = await store.CreateAsync(doomed).ConfigureAwait(false);
        _ = await store.CreateAsync(survivor).ConfigureAwait(false);
        _ = await store.UpdateStatusAsync(ExternalAppTestFixture.Transition(doomed.Id,
                             created.Version,
                             ExternalAppInstanceStatus.Uninstalling,
                             ExternalAppInstanceStatus.Installing,
                             ExternalAppInstanceEventKind.UninstallRequested))
                       .ConfigureAwait(false);
        var version = AssertEx.NotNull(await store.GetAsync(doomed.Id).ConfigureAwait(false)).Version;

        AssertEx.Equal(version,
            Convert.ToInt64(await fixture.RawScalarAsync("SELECT version FROM external_app_instances WHERE id = $id;",
                                             sqlCommand => sqlCommand.Parameters.AddWithValue("$id", doomed.Id))
                                         .ConfigureAwait(false),
                CultureInfo.InvariantCulture),
            "The CAS compares against the number the file holds, not the one the change tracker remembers.");

        // A stale delete first: the CAS guards the teardown as much as it guards a transition.
        AssertEx.False(await store.DeleteAsync(doomed.Id, expectedVersion: version + 5).ConfigureAwait(false));
        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("external_app_instances").ConfigureAwait(false));

        AssertEx.True(await store.DeleteAsync(doomed.Id, version).ConfigureAwait(false));

        AssertEx.Null(await store.GetAsync(doomed.Id).ConfigureAwait(false));
        AssertEx.Equal(expected: 0L,
            Convert.ToInt64(await fixture.RawScalarAsync("SELECT COUNT(*) FROM external_app_instance_events WHERE instance_id = $id;",
                                             sqlCommand => sqlCommand.Parameters.AddWithValue("$id", doomed.Id))
                                         .ConfigureAwait(false),
                CultureInfo.InvariantCulture),
            "Cascades never fire on this connection, so the events must be deleted explicitly or they are orphaned forever.");

        // The other instance is what makes the counts an assertion about scoping rather than about emptiness.
        AssertEx.NotNull(await store.GetAsync(survivor.Id).ConfigureAwait(false));
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("external_app_instance_events").ConfigureAwait(false), "and its own event survives.");
    }

    [Test]
    public async Task ListEventsAsync_PagesBySequenceAndRejectsANonPositiveLimit()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);
        var version = created.Version;
        var status = ExternalAppInstanceStatus.Installing;

        foreach (var next in new[]
                 {
                     ExternalAppInstanceStatus.Running,
                     ExternalAppInstanceStatus.Stopping,
                     ExternalAppInstanceStatus.Stopped
                 })
        {
            var applied = await store.UpdateStatusAsync(ExternalAppTestFixture.Transition(command.Id, version, next, status)).ConfigureAwait(false);
            AssertEx.True(applied.Applied);
            version = applied.Version;
            status = next;
        }

        var firstPage = await store.ListEventsAsync(command.Id, afterSequence: 0, limit: 2).ConfigureAwait(false);
        AssertEx.True(firstPage.Select(row => row.Sequence).SequenceEqual([1L, 2L]));

        var secondPage = await store.ListEventsAsync(command.Id, firstPage[^1].Sequence, limit: 2).ConfigureAwait(false);
        AssertEx.True(secondPage.Select(row => row.Sequence).SequenceEqual([3L, 4L]), "afterSequence is exclusive, which is what lets the hub resume without duplicating a row.");
        AssertEx.Empty(await store.ListEventsAsync(command.Id, secondPage[^1].Sequence, limit: 2).ConfigureAwait(false));

        _ = await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListEventsAsync(command.Id, afterSequence: 0, limit: 0),
                              "A non-positive limit is a caller bug, not an empty page.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task AppendedEventDetail_OverFourKibibytes_IsRejected()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);
        var oversized = new string('x', count: 4097);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() => store.UpdateStatusAsync(new ExternalAppStatusUpdate(command.Id,
                                            created.Version,
                                            new HashSet<ExternalAppInstanceStatus>
                                            {
                                                ExternalAppInstanceStatus.Installing
                                            },
                                            ExternalAppInstanceStatus.Failed,
                                            ExternalAppInstanceEventKind.Failed,
                                            oversized,
                                            OccurredAtUtc: 2_000)))
                                    .ConfigureAwait(false);
        AssertEx.True(failure.Message.Contains("4096", StringComparison.Ordinal), "The bound is the message's point.");

        // Refused BEFORE anything is written, and the same bound guards the create path.
        var snapshot = AssertEx.NotNull(await store.GetAsync(command.Id).ConfigureAwait(false));
        AssertEx.Equal(ExternalAppInstanceStatus.Installing, snapshot.Status);
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("external_app_instance_events").ConfigureAwait(false));

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => store.CreateAsync(ExternalAppTestFixture.Create(firstEventDetailJson: oversized))).ConfigureAwait(false);

        // Exactly at the bound is legal: it is 4096 bytes, not 4096 minus one.
        var atTheBound = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(command.Id,
                                        created.Version,
                                        new HashSet<ExternalAppInstanceStatus>
                                        {
                                            ExternalAppInstanceStatus.Installing
                                        },
                                        ExternalAppInstanceStatus.Failed,
                                        ExternalAppInstanceEventKind.Failed,
                                        new string('x', count: 4096),
                                        OccurredAtUtc: 2_000))
                                    .ConfigureAwait(false);
        AssertEx.True(atTheBound.Applied);
    }
}
