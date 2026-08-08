namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[NotInParallel]
public sealed class McpAgentRunStoreBoundaryTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task TombstoneReservationV1_CoversEveryRetainedFieldAtWorstCase()
    {
        await Task.CompletedTask;
        AssertEx.Equal(McpAgentRunPayloadProtector.FixedRecordOverheadBytes
                       + 16 + 32 + 4 + 4 + 8 + 128 + 8 + 8 + 8 + 8,
            McpAgentRunStore.TombstoneReservationBytesV1);
    }

    [Test]
    public async Task AdmitAsync_ConcurrentDistinctRequests_NeverExceedsNonterminalCapacity()
    {
        var databasePath = GetDatabasePath("concurrent-capacity.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);

        var starts = Enumerable.Range(start: 0, count: McpAgentRunStore.MaxNonterminalRuns + 8).Select(async index =>
        {
            await using var fixture = CreateFixture(databasePath);
            return await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), $"task-{index}"))
                                .ConfigureAwait(false);
        });

        var results = await Task.WhenAll(starts).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunStore.MaxNonterminalRuns,
            results.Count(result => result.Kind == McpAgentRunAdmissionKind.Accepted));
        AssertEx.Equal(expected: 8,
            results.Count(result => result.Kind == McpAgentRunAdmissionKind.CapacityExceeded
                                    && result.CapacityKind == McpAgentRunCapacityKind.NonterminalRuns));
        await using var verify = CreateFixture(databasePath);
        var ledger = await verify.Store.VerifyLedgerAsync().ConfigureAwait(false);
        AssertEx.True(ledger.IsConsistent, "Separate-connection admission must leave authoritative counters consistent.");
        AssertEx.Equal((long)McpAgentRunStore.MaxNonterminalRuns, ledger.Persisted.NonterminalRunCount);
    }

    [Test]
    public async Task AdmitAsync_ConcurrentDifferentFingerprintsForOneRequest_AcceptsOneAndConflictsTheOtherFingerprint()
    {
        var databasePath = GetDatabasePath("concurrent-conflict.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        var requestId = Guid.NewGuid();
        byte[] firstFingerprint;
        byte[] secondFingerprint;
        await using (var seed = CreateFixture(databasePath))
        {
            firstFingerprint = seed.Protector.ComputeRequestFingerprint(Encoding.UTF8.GetBytes("canonical-a"));
            secondFingerprint = seed.Protector.ComputeRequestFingerprint(Encoding.UTF8.GetBytes("canonical-b"));
        }

        var starts = Enumerable.Range(start: 0, count: 8).Select(async index =>
        {
            await using var fixture = CreateFixture(databasePath);
            var request = CreateAdmission(fixture.Protector, requestId, "same visible task") with
            {
                CanonicalRequest = index % 2 == 0 ? firstFingerprint : secondFingerprint
            };
            return await fixture.Store.AdmitAsync(request).ConfigureAwait(false);
        });

        var results = await Task.WhenAll(starts).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, results.Count(result => result.Kind == McpAgentRunAdmissionKind.Accepted));
        AssertEx.Equal(expected: 3, results.Count(result => result.Kind == McpAgentRunAdmissionKind.Existing));
        AssertEx.Equal(expected: 4, results.Count(result => result.Kind == McpAgentRunAdmissionKind.RequestIdConflict));
        await using var verify = CreateFixture(databasePath);
        AssertEx.Equal(expected: 1L, (await verify.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.IdentityCount);
    }

    [Test]
    public async Task AdmitAsync_WhenActivePayloadWouldReachExactLimit_AcceptsRequest()
    {
        var databasePath = GetDatabasePath("active-payload-exact.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var seed = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "seed")).ConfigureAwait(false);
        await SeedTerminalCountersAsync(databasePath,
                seed.Run!.RequestId,
                activePayloadBytes: McpAgentRunStore.MaxActivePayloadBytes - CalculateReservation(fixture.Protector, "boundary"),
                tombstoneLogicalBytes: McpAgentRunStore.TombstoneReservationBytesV1)
            .ConfigureAwait(false);

        var result = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "boundary")).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunAdmissionKind.Accepted, result.Kind);
        AssertEx.Equal(McpAgentRunStore.MaxActivePayloadBytes,
            (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.ActivePayloadBytes);
    }

    [Test]
    public async Task AdmitAsync_WhenActivePayloadWouldExceedLimit_RejectsWithoutChargingRequest()
    {
        var databasePath = GetDatabasePath("active-payload-over.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var seed = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "seed")).ConfigureAwait(false);
        await SeedTerminalCountersAsync(databasePath,
                seed.Run!.RequestId,
                activePayloadBytes: McpAgentRunStore.MaxActivePayloadBytes - CalculateReservation(fixture.Protector, "over-boundary") + 1,
                tombstoneLogicalBytes: McpAgentRunStore.TombstoneReservationBytesV1)
            .ConfigureAwait(false);

        var result = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "over-boundary")).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunAdmissionKind.CapacityExceeded, result.Kind);
        AssertEx.Equal(McpAgentRunCapacityKind.ActivePayloadBytes, result.CapacityKind);
        AssertEx.Equal(expected: 1L, (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.IdentityCount);
    }

    [Test]
    public async Task AdmitAsync_WhenTombstoneBytesWouldReachExactLimit_AcceptsRequest()
    {
        var databasePath = GetDatabasePath("tombstone-exact.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var seed = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "seed")).ConfigureAwait(false);
        var seedRun = AssertEx.NotNull(seed.Run, "Seed admission should return its persisted run.");
        const long tombstoneBytes = McpAgentRunStore.TombstoneReservationBytesV1;
        var activePayloadBytes = (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.ActivePayloadBytes;
        await SeedTerminalCountersAsync(databasePath,
            seedRun.RequestId,
            activePayloadBytes,
            McpAgentRunStore.MaxTombstoneLogicalBytes - tombstoneBytes).ConfigureAwait(false);

        var result = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "boundary")).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunAdmissionKind.Accepted, result.Kind);
        AssertEx.Equal(McpAgentRunStore.MaxTombstoneLogicalBytes,
            (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.TombstoneLogicalBytes);
    }

    [Test]
    public async Task AdmitAsync_WhenTombstoneBytesWouldExceedLimit_RejectsWithoutChargingRequest()
    {
        var databasePath = GetDatabasePath("tombstone-over.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var seed = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "seed")).ConfigureAwait(false);
        var seedRun = AssertEx.NotNull(seed.Run, "Seed admission should return its persisted run.");
        const long tombstoneBytes = McpAgentRunStore.TombstoneReservationBytesV1;
        var activePayloadBytes = (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.ActivePayloadBytes;
        await SeedTerminalCountersAsync(databasePath,
            seedRun.RequestId,
            activePayloadBytes,
            McpAgentRunStore.MaxTombstoneLogicalBytes - tombstoneBytes + 1).ConfigureAwait(false);

        var result = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "over-boundary")).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunAdmissionKind.CapacityExceeded, result.Kind);
        AssertEx.Equal(McpAgentRunCapacityKind.TombstoneBytes, result.CapacityKind);
        AssertEx.Equal(expected: 1L, (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.IdentityCount);
    }

    [Test]
    public async Task CompactExpiredPayloadsAsync_ReleasesAllActivePayloadAccounting()
    {
        var databasePath = GetDatabasePath("compact-accounting.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var requestId = Guid.NewGuid();
        var accepted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, "compact me")).ConfigureAwait(false);
        var claimed = await fixture.Store.TryClaimAsync(requestId, accepted.Run!.Version, claimedAtUtc: 2).ConfigureAwait(false);
        _ = await fixture.Store.TryFinalizeAsync(new McpAgentRunFinalization(requestId,
            claimed.Run!.Version,
            claimed.Run.ClaimToken!.Value,
            McpAgentRunStatus.Succeeded,
            McpAgentRunStopReason.None,
            FailureCode: null,
            Result: "answer",
            DisplayMessage: "complete",
            CompletedAtUtc: 3)).ConfigureAwait(false);

        var before = (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.ActivePayloadBytes;
        var compacted = await fixture.Store.CompactExpiredPayloadsAsync(expiresBeforeUtc: 100_000_000).ConfigureAwait(false);
        var after = await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false);

        AssertEx.True(before > 0, "A completed retained result must consume active payload accounting before compaction.");
        AssertEx.Equal(expected: 1, compacted);
        AssertEx.Equal(expected: 0L, after.Persisted.ActivePayloadBytes);
        AssertEx.True(after.IsConsistent, "Compaction must update the singleton ledger in the same transaction.");
    }

    [Test]
    public async Task RebuildLedgerAsync_ReconstructsEveryCounterFromAuthoritativeRows()
    {
        var databasePath = GetDatabasePath("rebuild-all-counters.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        _ = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "first")).ConfigureAwait(false);
        _ = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "second")).ConfigureAwait(false);
        var authoritative = (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Reconstructed;
        await ExecuteAsync(databasePath, """
                                         UPDATE mcp_agent_run_ledger
                                         SET nonterminal_run_count = 0, queued_run_count = 0, running_run_count = 0,
                                             identity_count = 0, active_payload_bytes = 0, tombstone_logical_bytes = 0;
                                         """).ConfigureAwait(false);

        var rebuilt = await fixture.Store.RebuildLedgerAsync(updatedAtUtc: 99).ConfigureAwait(false);

        AssertEx.Equal(authoritative.NonterminalRunCount, rebuilt.NonterminalRunCount);
        AssertEx.Equal(authoritative.IdentityCount, rebuilt.IdentityCount);
        AssertEx.Equal(authoritative.ActivePayloadBytes, rebuilt.ActivePayloadBytes);
        AssertEx.Equal(authoritative.TombstoneLogicalBytes, rebuilt.TombstoneLogicalBytes);
        AssertEx.Equal(expected: 99L, rebuilt.UpdatedAtUtc);
    }

    [Test]
    public async Task AdmitAsync_WhenLedgerSingletonIsMissingWithExistingIdentity_FailsClosed()
    {
        var databasePath = GetDatabasePath("missing-ledger.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        _ = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "existing")).ConfigureAwait(false);
        await ExecuteAsync(databasePath, "DELETE FROM mcp_agent_run_ledger WHERE id = 1;").ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "new")).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task AdmitAsync_DuplicateLookupPrecedesMissingLedgerValidation()
    {
        var databasePath = GetDatabasePath("duplicate-with-missing-ledger.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var requestId = Guid.NewGuid();
        var request = CreateAdmission(fixture.Protector, requestId, "existing");
        _ = await fixture.Store.AdmitAsync(request).ConfigureAwait(false);
        var expiredId = Guid.NewGuid();
        var expiredRequest = CreateAdmission(fixture.Protector, expiredId, "expired");
        var admittedExpired = await fixture.Store.AdmitAsync(expiredRequest).ConfigureAwait(false);
        var claimedExpired = await fixture.Store.TryClaimAsync(expiredId, admittedExpired.Run!.Version, claimedAtUtc: 2).ConfigureAwait(false);
        _ = await fixture.Store.TryFinalizeAsync(new McpAgentRunFinalization(expiredId,
            claimedExpired.Run!.Version,
            claimedExpired.Run.ClaimToken!.Value,
            McpAgentRunStatus.Succeeded,
            McpAgentRunStopReason.None,
            FailureCode: null,
            Result: "expired result",
            DisplayMessage: "done",
            CompletedAtUtc: 3)).ConfigureAwait(false);
        _ = await fixture.Store.CompactExpiredPayloadsAsync(3 + McpAgentRunStore.PayloadRetentionMilliseconds).ConfigureAwait(false);
        await ExecuteAsync(databasePath, "DELETE FROM mcp_agent_run_ledger WHERE id = 1;").ConfigureAwait(false);

        var existing = await fixture.Store.AdmitAsync(request).ConfigureAwait(false);
        var conflict = await fixture.Store.AdmitAsync(request with
        {
            CanonicalRequest = fixture.Protector.ComputeRequestFingerprint(Encoding.UTF8.GetBytes("different"))
        }).ConfigureAwait(false);
        var expired = await fixture.Store.AdmitAsync(expiredRequest).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunAdmissionKind.Existing, existing.Kind);
        AssertEx.Equal(McpAgentRunAdmissionKind.RequestIdConflict, conflict.Kind);
        AssertEx.Equal(McpAgentRunAdmissionKind.ResultExpired, expired.Kind);
    }

    [Test]
    public async Task QueuedRun_HasNoExpiry_AndReceivesFullRetentionAfterCompletion()
    {
        var databasePath = GetDatabasePath("completion-relative-retention.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var requestId = Guid.NewGuid();
        var admitted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, "wait in queue")).ConfigureAwait(false);
        AssertEx.Null(admitted.Run!.PayloadExpiresAtUtc);
        AssertEx.Equal(expected: 0, await fixture.Store.CompactExpiredPayloadsAsync(long.MaxValue).ConfigureAwait(false));

        const long completedAtUtc = 10L * 24 * 60 * 60 * 1000;
        var claimed = await fixture.Store.TryClaimAsync(requestId, admitted.Run.Version, claimedAtUtc: completedAtUtc - 1).ConfigureAwait(false);
        AssertEx.True(await fixture.Store.TryFinalizeAsync(new McpAgentRunFinalization(requestId,
            claimed.Run!.Version,
            claimed.Run.ClaimToken!.Value,
            McpAgentRunStatus.Succeeded,
            McpAgentRunStopReason.None,
            FailureCode: null,
            Result: "retained",
            DisplayMessage: "done",
            CompletedAtUtc: completedAtUtc)).ConfigureAwait(false));

        var terminal = AssertEx.NotNull(await fixture.Store.GetAsync(requestId).ConfigureAwait(false));
        AssertEx.Equal(completedAtUtc + McpAgentRunStore.PayloadRetentionMilliseconds, terminal.PayloadExpiresAtUtc);
        AssertEx.Equal(expected: 0,
            await fixture.Store.CompactExpiredPayloadsAsync(terminal.PayloadExpiresAtUtc!.Value - 1).ConfigureAwait(false));
        AssertEx.Equal(expected: 1,
            await fixture.Store.CompactExpiredPayloadsAsync(terminal.PayloadExpiresAtUtc.Value).ConfigureAwait(false));
    }

    [Test]
    public async Task CompactExpiredPayloadsAsync_LeavesOnlyAccountedMinimalTombstone()
    {
        var databasePath = GetDatabasePath("minimal-tombstone.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var requestId = Guid.NewGuid();
        var admitted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, "compact all execution data") with
        {
            ModelOverrideId = "override-model",
            WorkspaceId = Guid.NewGuid()
        }).ConfigureAwait(false);
        var claimed = await fixture.Store.TryClaimAsync(requestId, admitted.Run!.Version, claimedAtUtc: 2).ConfigureAwait(false);
        _ = await fixture.Store.TryFinalizeAsync(new McpAgentRunFinalization(requestId,
            claimed.Run!.Version,
            claimed.Run.ClaimToken!.Value,
            McpAgentRunStatus.Failed,
            McpAgentRunStopReason.None,
            FailureCode: "stable_failure",
            Result: null,
            DisplayMessage: "safe display",
            CompletedAtUtc: 3)).ConfigureAwait(false);
        _ = await fixture.Store.CompactExpiredPayloadsAsync(3 + McpAgentRunStore.PayloadRetentionMilliseconds).ConfigureAwait(false);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT claim_token, stop_requested_at_utc, agent_definition_id, agent_definition_version,
                                  model_id, model_override_id, workspace_id, binding_fingerprint, task_payload, instructions_payload,
                                  result_payload, display_payload, claimed_at_utc, payload_expires_at_utc,
                                  reserved_active_payload_bytes, active_payload_bytes, tombstone_logical_bytes, compacted_at_utc,
                                  failure_code, stop_reason, status
                              FROM mcp_agent_runs WHERE request_id = $requestId;
                              """;
        command.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        AssertEx.True(await reader.ReadAsync().ConfigureAwait(false));
        for (var ordinal = 0; ordinal < 14; ordinal++)
        {
            AssertEx.True(await reader.IsDBNullAsync(ordinal).ConfigureAwait(false),
                $"Compacted variable column {ordinal} must be NULL.");
        }

        AssertEx.Equal(expected: 0L, reader.GetInt64(14));
        AssertEx.Equal(expected: 0L, reader.GetInt64(15));
        AssertEx.Equal((long)McpAgentRunStore.TombstoneReservationBytesV1, reader.GetInt64(16));
        AssertEx.Equal(3 + McpAgentRunStore.PayloadRetentionMilliseconds, reader.GetInt64(17));
        AssertEx.Equal("stable_failure", reader.GetString(18));
        AssertEx.Equal((long)McpAgentRunStopReason.None, reader.GetInt64(19));
        AssertEx.Equal((long)McpAgentRunStatus.Failed, reader.GetInt64(20));
    }

    [Test]
    public async Task CompactExpiredPayloadsAsync_WhenLedgerUpdateAborts_RollsBackCompactedRowAndLedgerTogether()
    {
        var databasePath = GetDatabasePath("compaction-rollback.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var requestId = Guid.NewGuid();
        var admitted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, "retain after abort")).ConfigureAwait(false);
        var claimed = await fixture.Store.TryClaimAsync(requestId, admitted.Run!.Version, claimedAtUtc: 2).ConfigureAwait(false);
        _ = await fixture.Store.TryFinalizeAsync(new McpAgentRunFinalization(requestId,
            claimed.Run!.Version,
            claimed.Run.ClaimToken!.Value,
            McpAgentRunStatus.Succeeded,
            McpAgentRunStopReason.None,
            FailureCode: null,
            Result: "answer",
            DisplayMessage: "done",
            CompletedAtUtc: 3)).ConfigureAwait(false);
        var retainedBeforeFailure = AssertEx.NotNull(await fixture.Store.GetAsync(requestId).ConfigureAwait(false));
        var before = await fixture.Store.GetLedgerSnapshotAsync().ConfigureAwait(false);
        await ExecuteAsync(databasePath, """
                                         CREATE TRIGGER abort_mcp_compaction_ledger_update
                                         BEFORE UPDATE ON mcp_agent_run_ledger
                                         WHEN NEW.active_payload_bytes < OLD.active_payload_bytes
                                         BEGIN
                                             SELECT RAISE(ABORT, 'forced ledger accounting abort after row compaction');
                                         END;
                                         """).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(async () =>
        {
            _ = await fixture.Store.CompactExpiredPayloadsAsync(3 + McpAgentRunStore.PayloadRetentionMilliseconds).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var after = await fixture.Store.GetLedgerSnapshotAsync().ConfigureAwait(false);
        var retained = AssertEx.NotNull(await fixture.Store.GetAsync(requestId).ConfigureAwait(false));
        AssertEx.Equal(before.Counters.ActivePayloadBytes, after.Counters.ActivePayloadBytes);
        AssertEx.Equal(before.Counters.TombstoneLogicalBytes, after.Counters.TombstoneLogicalBytes);
        AssertEx.Equal(retainedBeforeFailure.Version, retained.Version);
        AssertEx.Equal(retainedBeforeFailure.PayloadExpiresAtUtc, retained.PayloadExpiresAtUtc);
        AssertEx.Equal("answer", retained.Result);
        AssertEx.Null(retained.CompactedAtUtc);
    }

    [Test]
    public async Task EncryptedPayloadSentinels_DoNotAppearInSqliteWalOrSharedMemoryFiles()
    {
        var databasePath = GetDatabasePath("encrypted-file-scan.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        const string task = "task-sentinel-f4f5c74d-d30a-4882-8421-f4cb5f0db616";
        const string instructions = "instructions-sentinel-56e3cc04-e155-46a2-9d3b-d34b6398bc18";
        const string result = "result-sentinel-da3a50cd-9900-48a2-9e52-e427199ff15a";
        const string display = "display-sentinel-6b07e768-1507-4c40-9cea-c3ad517ed5f6";
        var requestId = Guid.NewGuid();
        var admitted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, task) with
        {
            Instructions = instructions
        }).ConfigureAwait(false);
        var claimed = await fixture.Store.TryClaimAsync(requestId, admitted.Run!.Version, claimedAtUtc: 2).ConfigureAwait(false);
        _ = await fixture.Store.TryFinalizeAsync(new McpAgentRunFinalization(requestId,
            claimed.Run!.Version,
            claimed.Run.ClaimToken!.Value,
            McpAgentRunStatus.Succeeded,
            McpAgentRunStopReason.None,
            FailureCode: null,
            Result: result,
            DisplayMessage: display,
            CompletedAtUtc: 3)).ConfigureAwait(false);

        foreach (var path in new[]
                 {
                     databasePath,
                     databasePath + "-wal",
                     databasePath + "-shm"
                 }.Where(File.Exists))
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            AssertEx.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(task)) >= 0, $"Task plaintext leaked into {Path.GetFileName(path)}.");
            AssertEx.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(instructions)) >= 0, $"Instructions plaintext leaked into {Path.GetFileName(path)}.");
            AssertEx.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(result)) >= 0, $"Result plaintext leaked into {Path.GetFileName(path)}.");
            AssertEx.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(display)) >= 0, $"Display plaintext leaked into {Path.GetFileName(path)}.");
        }
    }

    [Test]
    public async Task ReconcileInterruptedRunsAsync_LeavesQueuedWorkReplayableAndTerminalizesOnlyClaimedWork()
    {
        var databasePath = GetDatabasePath("queued-recovery.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var queuedId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        _ = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, queuedId, "resume after restart")).ConfigureAwait(false);
        var running = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, runningId, "never replay")).ConfigureAwait(false);
        _ = await fixture.Store.TryClaimAsync(runningId, running.Run!.Version, claimedAtUtc: 2).ConfigureAwait(false);

        var reconciled = await fixture.Store.ReconcileInterruptedRunsAsync(completedAtUtc: 3).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, reconciled);
        AssertEx.Equal(McpAgentRunStatus.Queued, (await fixture.Store.GetAsync(queuedId).ConfigureAwait(false))!.Status);
        var interrupted = AssertEx.NotNull(await fixture.Store.GetAsync(runningId).ConfigureAwait(false));
        AssertEx.Equal(McpAgentRunStatus.Interrupted, interrupted.Status);
        AssertEx.Equal("interrupted", interrupted.FailureCode!);
    }

    private static async Task InitializeDatabaseAsync(string databasePath)
    {
        using var keyHolder = new FixedNodeSqliteKeyHolder();
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    private static StoreFixture CreateFixture(string databasePath) =>
        new(databasePath);

    private static McpAgentRunAdmissionRequest CreateAdmission(McpAgentRunPayloadProtector protector, Guid requestId, string task) =>
        new(requestId,
            protector.ComputeRequestFingerprint(Encoding.UTF8.GetBytes($"canonical:{task}")),
            task,
            Instructions: "read only",
            AgentDefinitionId: Guid.Parse("61f97d46-14bb-47c0-8a58-ac66f2940e76"),
            AgentDefinitionVersion: 7,
            ModelId: "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M",
            ModelOverrideId: null,
            WorkspaceId: null,
            BindingFingerprint: SHA256.HashData(Encoding.UTF8.GetBytes("binding")),
            CreatedAtUtc: 1);

    private static long CalculateReservation(McpAgentRunPayloadProtector protector, string task) =>
        checked((long)Encoding.UTF8.GetByteCount(task)
                + Encoding.UTF8.GetByteCount("read only")
                + ((long)McpAgentRunStore.MaxResultCharacters * 4)
                + McpAgentRunStore.MaxDisplayUtf8Bytes
                + (4L * protector.FixedEnvelopeOverheadBytes)
                + McpAgentRunPayloadProtector.FixedRecordOverheadBytes);

    private static async Task SeedTerminalCountersAsync(string databasePath,
        Guid requestId,
        long activePayloadBytes,
        long tombstoneLogicalBytes)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using (var run = connection.CreateCommand())
        {
            run.Transaction = transaction;
            run.CommandText = """
                              UPDATE mcp_agent_runs
                              SET status = $status, completed_at_utc = 2, active_payload_bytes = $activePayloadBytes,
                                  tombstone_logical_bytes = $tombstoneLogicalBytes
                              WHERE request_id = $requestId;
                              """;
            run.Parameters.AddWithValue("$status", (int)McpAgentRunStatus.Succeeded);
            run.Parameters.AddWithValue("$activePayloadBytes", activePayloadBytes);
            run.Parameters.AddWithValue("$tombstoneLogicalBytes", tombstoneLogicalBytes);
            run.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            _ = await run.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var ledger = connection.CreateCommand())
        {
            ledger.Transaction = transaction;
            ledger.CommandText = """
                                 UPDATE mcp_agent_run_ledger
                                 SET nonterminal_run_count = 0, queued_run_count = 0, running_run_count = 0,
                                     active_payload_bytes = $activePayloadBytes,
                                     tombstone_logical_bytes = $tombstoneLogicalBytes
                                 WHERE id = 1;
                                 """;
            ledger.Parameters.AddWithValue("$activePayloadBytes", activePayloadBytes);
            ledger.Parameters.AddWithValue("$tombstoneLogicalBytes", tombstoneLogicalBytes);
            _ = await ledger.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test-only fixed SQL text is supplied exclusively by this test class and never contains user input.")]
    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly FixedNodeSqliteKeyHolder _keyHolder = new();
        private readonly NodeChatDbContext _context;

        public StoreFixture(string databasePath)
        {
            _context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
            Protector = new McpAgentRunPayloadProtector(_keyHolder, new AesGcmNodeAeadCipher());
            Store = new McpAgentRunStore(_context, Protector);
        }

        public McpAgentRunPayloadProtector Protector { get; }

        public McpAgentRunStore Store { get; }

        public async ValueTask DisposeAsync()
        {
            Protector.Dispose();
            await _context.DisposeAsync().ConfigureAwait(false);
            _keyHolder.Dispose();
        }
    }

    private sealed class FixedNodeSqliteKeyHolder : INodeSqliteKeyHolder
    {
        private byte[]? _key = SHA256.HashData(Encoding.UTF8.GetBytes("mcp-agent-run-boundary-test-key"));

        public ReadOnlyMemory<byte> Key => _key ?? throw new ObjectDisposedException(nameof(FixedNodeSqliteKeyHolder));

        public void Dispose()
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }
        }
    }
}
