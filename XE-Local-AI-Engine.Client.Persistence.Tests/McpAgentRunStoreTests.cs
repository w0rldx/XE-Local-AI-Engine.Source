namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class McpAgentRunStoreTests : IDisposable
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
    public async Task AdmitAsync_SameFingerprintIsIdempotent_DifferentFingerprintConflicts()
    {
        var databasePath = GetDatabasePath("idempotency.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var requestId = Guid.NewGuid();
        var request = CreateAdmission(fixture.Protector, requestId, "inspect the scheduler");

        var accepted = await fixture.Store.AdmitAsync(request).ConfigureAwait(false);
        var existing = await fixture.Store.AdmitAsync(request).ConfigureAwait(false);
        var conflict = await fixture.Store.AdmitAsync(request with
        {
            CanonicalRequest = fixture.Protector.ComputeRequestFingerprint(Encoding.UTF8.GetBytes("different canonical request"))
        }).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunAdmissionKind.Accepted, accepted.Kind);
        AssertEx.Equal(McpAgentRunAdmissionKind.Existing, existing.Kind);
        AssertEx.Equal(McpAgentRunAdmissionKind.RequestIdConflict, conflict.Kind);
        AssertEx.True(accepted.Run!.RequestFingerprint.Span.SequenceEqual(existing.Run!.RequestFingerprint.Span),
            "Idempotent lookup must return the permanent keyed fingerprint.");
        AssertEx.Equal(expected: 1L, (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.IdentityCount);
    }

    [Test]
    public async Task AdmitAsync_EnforcesBoundedAsciiAgenticPrefixAlphabet()
    {
        var databasePath = GetDatabasePath("invalid-agentic-prefix.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);

        foreach (var prefix in new[] { "xemcp bad", "xemcp.bad" })
        {
            var request = CreateAdmission(fixture.Protector, Guid.NewGuid(), "inspect") with
            {
                IsAgenticAutoApprove = true,
                RequestingKeyPrefix = prefix
            };

            _ = await AssertEx.ThrowsAsync<ArgumentException>(() => fixture.Store.AdmitAsync(request)).ConfigureAwait(false);
        }

        var valid = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "inspect valid") with
        {
            IsAgenticAutoApprove = true,
            RequestingKeyPrefix = "xemcp_Abc-123"
        }).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunAdmissionKind.Accepted, valid.Kind);
        AssertEx.Equal(expected: 1L, (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.IdentityCount);
    }

    [Test]
    public async Task AdmitAsync_ConcurrentDuplicate_HasOneIdentityAndNoDoubleReservation()
    {
        var databasePath = GetDatabasePath("concurrent-duplicate.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        var requestId = Guid.NewGuid();
        byte[] fingerprint;
        await using (var seed = CreateFixture(databasePath))
        {
            fingerprint = seed.Protector.ComputeRequestFingerprint(Encoding.UTF8.GetBytes("shared canonical request"));
        }

        var starts = Enumerable.Range(start: 0, count: 8).Select(async _ =>
        {
            await using var fixture = CreateFixture(databasePath);
            return await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, "same task") with
            {
                CanonicalRequest = fingerprint
            }).ConfigureAwait(false);
        });

        var results = await Task.WhenAll(starts).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, results.Count(result => result.Kind == McpAgentRunAdmissionKind.Accepted));
        AssertEx.Equal(expected: 7, results.Count(result => result.Kind == McpAgentRunAdmissionKind.Existing));

        await using var verify = CreateFixture(databasePath);
        var ledger = await verify.Store.VerifyLedgerAsync().ConfigureAwait(false);
        AssertEx.True(ledger.IsConsistent, "Concurrent admission must leave the singleton counters consistent.");
        AssertEx.Equal(expected: 1L, ledger.Persisted.IdentityCount);
        AssertEx.Equal(expected: 1L, ledger.Persisted.NonterminalRunCount);
    }

    [Test]
    public async Task AdmitAsync_AtNonterminalLimit_RejectsWithoutChargingIdentityOrPayload()
    {
        var databasePath = GetDatabasePath("nonterminal-capacity.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        for (var index = 0; index < McpAgentRunStore.MaxNonterminalRuns; index++)
        {
            var admitted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), $"task-{index}")).ConfigureAwait(false);
            AssertEx.Equal(McpAgentRunAdmissionKind.Accepted, admitted.Kind);
        }

        var before = (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted;
        var rejected = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "over capacity")).ConfigureAwait(false);
        var after = (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted;

        AssertEx.Equal(McpAgentRunAdmissionKind.CapacityExceeded, rejected.Kind);
        AssertEx.Equal(McpAgentRunCapacityKind.NonterminalRuns, rejected.CapacityKind);
        AssertEx.Equal(before.IdentityCount, after.IdentityCount);
        AssertEx.Equal(before.ActivePayloadBytes, after.ActivePayloadBytes);
    }

    [Test]
    public async Task AdmitAsync_WhenSingletonCountersDrift_UsesSerializedCountersUntilStartupRepair()
    {
        var databasePath = GetDatabasePath("counter-drift.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        _ = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "first")).ConfigureAwait(false);

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE mcp_agent_run_ledger SET identity_count = identity_count + 1 WHERE id = 1;";
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var admitted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "second")).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunAdmissionKind.Accepted, admitted.Kind);
        AssertEx.False((await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).IsConsistent,
            "Steady-state mutation must not reconstruct the full retained-run table under its write transaction.");

        var rebuilt = await fixture.Store.RebuildLedgerAsync(updatedAtUtc: 10).ConfigureAwait(false);
        AssertEx.Equal(expected: 2L, rebuilt.IdentityCount);
        AssertEx.True((await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).IsConsistent,
            "The explicit startup repair API must reconstruct counters from authoritative rows.");
    }

    [Test]
    public async Task ListAsync_UsesMetadataOnlyProjectionAndDoesNotReturnPayloads()
    {
        var databasePath = GetDatabasePath("metadata-list.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        _ = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, Guid.NewGuid(), "private task")).ConfigureAwait(false);

        var runs = await fixture.Store.ListAsync(limit: 10).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, runs.Count);
        AssertEx.Null(runs[0].Task);
        AssertEx.Null(runs[0].Instructions);
        AssertEx.Null(runs[0].Result);
        AssertEx.Null(runs[0].DisplayMessage);
        AssertEx.False(McpAgentRunStore.MetadataSelectColumns.Contains("task_payload", StringComparison.Ordinal),
            "Metadata listing SQL must not select the encrypted task BLOB.");
        AssertEx.False(McpAgentRunStore.MetadataSelectColumns.Contains("instructions_payload", StringComparison.Ordinal),
            "Metadata listing SQL must not select the encrypted instructions BLOB.");
        AssertEx.False(McpAgentRunStore.MetadataSelectColumns.Contains("result_payload", StringComparison.Ordinal),
            "Metadata listing SQL must not select the encrypted result BLOB.");
        AssertEx.False(McpAgentRunStore.MetadataSelectColumns.Contains("display_payload", StringComparison.Ordinal),
            "Metadata listing SQL must not select the encrypted display BLOB.");
    }

    [Test]
    public async Task Payloads_AreEncryptedWithFieldBinding_AndTamperFailsClosed()
    {
        var databasePath = GetDatabasePath("payload-protection.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var requestId = Guid.NewGuid();
        const string secret = "private-task-material-93d2828b-84c8-440b-a187-a972095577e4";
        _ = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, secret)).ConfigureAwait(false);

        byte[] envelope;
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT task_payload FROM mcp_agent_runs WHERE request_id = $requestId;";
            command.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            envelope = (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        }

        AssertEx.False(envelope.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0,
            "The task must not appear in plaintext inside the stored envelope.");
        AssertEx.Equal(secret, Encoding.UTF8.GetString(fixture.Protector.Unprotect(requestId, "task", envelope)));
        _ = await AssertEx.ThrowsAsync<AuthenticationTagMismatchException>(() =>
        {
            _ = fixture.Protector.Unprotect(requestId, "result", envelope);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        envelope[^1] ^= 0x01;
        _ = await AssertEx.ThrowsAsync<AuthenticationTagMismatchException>(() =>
        {
            _ = fixture.Protector.Unprotect(requestId, "task", envelope);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task ClaimStopFinalizeAndCompaction_EnforceVersionedLifecycleAndPermanentIdentity()
    {
        var databasePath = GetDatabasePath("lifecycle.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        await using var fixture = CreateFixture(databasePath);
        var requestId = Guid.NewGuid();
        var accepted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, "long-running review")).ConfigureAwait(false);
        var claimed = await fixture.Store.TryClaimAsync(requestId, accepted.Run!.Version, claimedAtUtc: 20).ConfigureAwait(false);
        var stopped = await fixture.Store.RequestStopAsync(requestId,
            claimed.Run!.Version,
            McpAgentRunStopReason.WatchdogExpired,
            requestedAtUtc: 30).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunClaimKind.Claimed, claimed.Kind);
        AssertEx.Equal(McpAgentRunStopKind.Requested, stopped.Kind);
        AssertEx.False(await fixture.Store.TryFinalizeAsync(new McpAgentRunFinalization(requestId,
                claimed.Run.Version,
                claimed.Run.ClaimToken!.Value,
                McpAgentRunStatus.Succeeded,
                McpAgentRunStopReason.None,
                FailureCode: null,
                Result: "late success",
                DisplayMessage: null,
                CompletedAtUtc: 31)).ConfigureAwait(false),
            "A stale normal completion must lose after the stop marker bumps the version.");

        AssertEx.True(await fixture.Store.TryFinalizeAsync(new McpAgentRunFinalization(requestId,
                stopped.Run!.Version,
                claimed.Run.ClaimToken.Value,
                McpAgentRunStatus.Failed,
                McpAgentRunStopReason.WatchdogExpired,
                "watchdog_expired",
                Result: null,
                DisplayMessage: "Run exceeded its time limit.",
                CompletedAtUtc: 32)).ConfigureAwait(false),
            "The marker-matched worker finalization should commit exactly once.");

        var beforeCompact = AssertEx.NotNull(await fixture.Store.GetAsync(requestId).ConfigureAwait(false));
        AssertEx.Equal(McpAgentRunStatus.Failed, beforeCompact.Status);
        AssertEx.Equal("watchdog_expired", beforeCompact.FailureCode);
        AssertEx.Equal(expected: 1, await fixture.Store.CompactExpiredPayloadsAsync(expiresBeforeUtc: 200_000_000).ConfigureAwait(false));
        var expired = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, requestId, "long-running review")).ConfigureAwait(false);
        AssertEx.Equal(McpAgentRunAdmissionKind.ResultExpired, expired.Kind);
        AssertEx.True(expired.Run!.PayloadExpired, "Compaction should retain identity while removing encrypted payloads.");
        AssertEx.Equal(expected: 1L, (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.IdentityCount);
    }

    [Test]
    public async Task ReconcileInterruptedRuns_MapsPersistedStopMarkersWithoutReplayingRunningRows()
    {
        var databasePath = GetDatabasePath("recovery.sqlite");
        await InitializeDatabaseAsync(databasePath).ConfigureAwait(false);
        var ids = new[]
        {
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        };
        await using var fixture = CreateFixture(databasePath);

        for (var index = 0; index < ids.Length; index++)
        {
            var accepted = await fixture.Store.AdmitAsync(CreateAdmission(fixture.Protector, ids[index], $"task-{index}")).ConfigureAwait(false);
            var claimed = await fixture.Store.TryClaimAsync(ids[index], accepted.Run!.Version, claimedAtUtc: 20 + index).ConfigureAwait(false);
            if (index == 0)
            {
                _ = await fixture.Store.RequestStopAsync(ids[index], claimed.Run!.Version, McpAgentRunStopReason.UserCancellation, requestedAtUtc: 30).ConfigureAwait(false);
            }
            else if (index == 1)
            {
                _ = await fixture.Store.RequestStopAsync(ids[index], claimed.Run!.Version, McpAgentRunStopReason.WatchdogExpired, requestedAtUtc: 31).ConfigureAwait(false);
            }
        }

        AssertEx.Equal(expected: 3, await fixture.Store.ReconcileInterruptedRunsAsync(completedAtUtc: 40).ConfigureAwait(false));
        AssertEx.Equal(McpAgentRunStatus.Cancelled, (await fixture.Store.GetAsync(ids[0]).ConfigureAwait(false))!.Status);
        var watchdog = AssertEx.NotNull(await fixture.Store.GetAsync(ids[1]).ConfigureAwait(false));
        AssertEx.Equal(McpAgentRunStatus.Failed, watchdog.Status);
        AssertEx.Equal("watchdog_expired", watchdog.FailureCode);
        AssertEx.Equal(McpAgentRunStatus.Interrupted, (await fixture.Store.GetAsync(ids[2]).ConfigureAwait(false))!.Status);
        AssertEx.Equal(expected: 0L, (await fixture.Store.VerifyLedgerAsync().ConfigureAwait(false)).Persisted.NonterminalRunCount);
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

    private static McpAgentRunAdmissionRequest CreateAdmission(McpAgentRunPayloadProtector protector, Guid requestId, string task)
    {
        return new McpAgentRunAdmissionRequest(requestId,
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
        private byte[]? _key = SHA256.HashData(Encoding.UTF8.GetBytes("mcp-agent-run-test-key"));

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
