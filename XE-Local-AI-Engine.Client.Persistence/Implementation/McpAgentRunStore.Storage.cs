namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class McpAgentRunStore
{
    private const string SelectColumns = """
                                         SELECT request_id, request_fingerprint, accounting_version, status, version, claim_token, stop_reason,
                                             stop_requested_at_utc, agent_definition_id, agent_definition_version, model_id, model_override_id, workspace_id,
                                             is_agentic_auto_approve, requesting_key_prefix, binding_fingerprint,
                                             task_payload, instructions_payload, result_payload, display_payload, failure_code,
                                             reserved_active_payload_bytes, active_payload_bytes, tombstone_logical_bytes, created_at_utc,
                                             claimed_at_utc, completed_at_utc, payload_expires_at_utc, compacted_at_utc
                                         FROM mcp_agent_runs
                                         """;

    internal const string MetadataSelectColumns = """
                                                  SELECT request_id, request_fingerprint, accounting_version, status, version, claim_token, stop_reason,
                                                      stop_requested_at_utc, agent_definition_id, agent_definition_version, model_id, model_override_id, workspace_id,
                                                      is_agentic_auto_approve, requesting_key_prefix, binding_fingerprint,
                                                      NULL, NULL, NULL, NULL, failure_code,
                                                      reserved_active_payload_bytes, active_payload_bytes, tombstone_logical_bytes, created_at_utc,
                                                      claimed_at_utc, completed_at_utc, payload_expires_at_utc, compacted_at_utc
                                                  FROM mcp_agent_runs
                                                  """;

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await NodeSqlitePragmas.OpenAndConfigureAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<McpAgentRunRow?> ReadRunAsync(SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid requestId,
        bool includePayload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, SelectColumns + " WHERE request_id = $requestId;");
        Add(command, "$requestId", requestId.ToString("D", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRow(reader, includePayload) : null;
    }

    private static McpAgentRunRow ReadRow(SqliteDataReader reader, bool includePayload)
    {
        var row = new McpAgentRunRow(Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            (byte[])reader.GetValue(1),
            reader.GetInt32(2),
            (McpAgentRunStatus)reader.GetInt32(3),
            reader.GetInt64(4),
            GetNullableGuid(reader, 5),
            (McpAgentRunStopReason)reader.GetInt32(6),
            GetNullableInt64(reader, 7),
            GetNullableGuid(reader, 8),
            GetNullableInt64(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            GetNullableGuid(reader, 12),
            reader.GetInt32(13) != 0,
            reader.IsDBNull(14) ? null : reader.GetString(14),
            GetNullableBytes(reader, 15),
            includePayload ? GetNullableBytes(reader, 16) : null,
            includePayload ? GetNullableBytes(reader, 17) : null,
            includePayload ? GetNullableBytes(reader, 18) : null,
            includePayload ? GetNullableBytes(reader, 19) : null,
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.GetInt64(21),
            reader.GetInt64(22),
            reader.GetInt64(23),
            reader.GetInt64(24),
            GetNullableInt64(reader, 25),
            GetNullableInt64(reader, 26),
            GetNullableInt64(reader, 27),
            GetNullableInt64(reader, 28));
        if (row.StoredAccountingVersion != AccountingVersion)
        {
            throw new InvalidOperationException($"Unsupported MCP run accounting version {row.StoredAccountingVersion}.");
        }

        return row;
    }

    private McpAgentRunRecord ToRecord(McpAgentRunRow row)
    {
        return new McpAgentRunRecord(row.RequestId,
            row.RequestFingerprint.ToArray(),
            row.Status,
            row.Version,
            row.ClaimToken,
            row.StopReason,
            row.StopRequestedAtUtc,
            row.AgentDefinitionId,
            row.AgentDefinitionVersion,
            row.ModelId,
            row.ModelOverrideId,
            row.WorkspaceId,
            row.BindingFingerprint?.ToArray(),
            UnprotectString(row.RequestId, "task", row.TaskPayload),
            UnprotectString(row.RequestId, "instructions", row.InstructionsPayload),
            UnprotectString(row.RequestId, "result", row.ResultPayload),
            UnprotectString(row.RequestId, "display", row.DisplayPayload),
            row.FailureCode,
            row.CreatedAtUtc,
            row.ClaimedAtUtc,
            row.CompletedAtUtc,
            row.PayloadExpiresAtUtc,
            row.CompactedAtUtc,
            PayloadExpired: row.ActivePayloadBytes == 0,
            IsAgenticAutoApprove: row.IsAgenticAutoApprove,
            RequestingKeyPrefix: row.RequestingKeyPrefix);
    }

    private string? UnprotectString(Guid requestId, string fieldName, byte[]? payload)
    {
        return payload is null ? null : Encoding.UTF8.GetString(_protector.Unprotect(requestId, fieldName, payload));
    }

    private static long CalculateStoredPayloadBytes(McpAgentRunRow row)
    {
        return checked((long)(row.TaskPayload?.Length ?? 0)
                       + (row.InstructionsPayload?.Length ?? 0)
                       + (row.ResultPayload?.Length ?? 0)
                       + (row.DisplayPayload?.Length ?? 0)
                       + McpAgentRunPayloadProtector.FixedRecordOverheadBytes);
    }

    private static async Task<McpAgentRunLedgerCounters> LoadRequiredLedgerAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ledger = await TryLoadLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("The MCP run ledger singleton is missing.");
        ValidateLedgerVersion(ledger);
        return ledger;
    }

    private static async Task<McpAgentRunLedgerCounters?> TryLoadLedgerAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
                                                                         SELECT accounting_version, nonterminal_run_count, queued_run_count, running_run_count, identity_count, active_payload_bytes,
                                                                             tombstone_logical_bytes, updated_at_utc
                                                                         FROM mcp_agent_run_ledger WHERE id = 1;
                                                                         """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new McpAgentRunLedgerCounters(reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    private static async Task<McpAgentRunLedgerCounters> ReconstructCountersAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        long updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
                                                                         SELECT
                                                                             COALESCE(SUM(CASE WHEN status IN ($queued, $running) THEN 1 ELSE 0 END), 0),
                                                                             COALESCE(SUM(CASE WHEN status = $queued THEN 1 ELSE 0 END), 0),
                                                                             COALESCE(SUM(CASE WHEN status = $running THEN 1 ELSE 0 END), 0),
                                                                             COUNT(*),
                                                                             COALESCE(SUM(active_payload_bytes), 0),
                                                                             COALESCE(SUM(tombstone_logical_bytes), 0),
                                                                             COALESCE(MIN(accounting_version), $accountingVersion),
                                                                             COALESCE(MAX(accounting_version), $accountingVersion)
                                                                         FROM mcp_agent_runs;
                                                                         """);
        Add(command, "$queued", (int)McpAgentRunStatus.Queued);
        Add(command, "$running", (int)McpAgentRunStatus.Running);
        Add(command, "$accountingVersion", AccountingVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var minVersion = reader.GetInt32(6);
        var maxVersion = reader.GetInt32(7);
        if (minVersion != AccountingVersion || maxVersion != AccountingVersion)
        {
            throw new InvalidOperationException("MCP run rows use an unsupported accounting version.");
        }

        return new McpAgentRunLedgerCounters(AccountingVersion,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            updatedAtUtc);
    }

    private static bool CountersEqual(McpAgentRunLedgerCounters left, McpAgentRunLedgerCounters right)
    {
        return left.AccountingVersion == right.AccountingVersion
               && left.NonterminalRunCount == right.NonterminalRunCount
               && left.QueuedRunCount == right.QueuedRunCount
               && left.RunningRunCount == right.RunningRunCount
               && left.IdentityCount == right.IdentityCount
               && left.ActivePayloadBytes == right.ActivePayloadBytes
               && left.TombstoneLogicalBytes == right.TombstoneLogicalBytes;
    }

    private static Task UpdateLedgerAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        McpAgentRunLedgerCounters counters,
        CancellationToken cancellationToken)
    {
        if (counters.NonterminalRunCount < 0 || counters.QueuedRunCount < 0 || counters.RunningRunCount < 0
            || counters.IdentityCount < 0 || counters.ActivePayloadBytes < 0 || counters.TombstoneLogicalBytes < 0)
        {
            throw new InvalidOperationException("An MCP run ledger transition would produce a negative counter.");
        }

        if (counters.NonterminalRunCount != counters.QueuedRunCount + counters.RunningRunCount)
        {
            throw new InvalidOperationException("MCP run nonterminal accounting does not equal queued plus running.");
        }

        return UpsertLedgerAsync(connection, transaction, counters, cancellationToken);
    }

    private static async Task UpsertLedgerAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        McpAgentRunLedgerCounters counters,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
                                                                         INSERT INTO mcp_agent_run_ledger (
                                                                             id, accounting_version, nonterminal_run_count, queued_run_count, running_run_count, identity_count, active_payload_bytes,
                                                                             tombstone_logical_bytes, updated_at_utc)
                                                                         VALUES (1, $accountingVersion, $nonterminal, $queued, $running, $identities, $activeBytes, $tombstoneBytes, $updatedAtUtc)
                                                                         ON CONFLICT(id) DO UPDATE SET
                                                                             accounting_version = excluded.accounting_version,
                                                                             nonterminal_run_count = excluded.nonterminal_run_count,
                                                                             queued_run_count = excluded.queued_run_count,
                                                                             running_run_count = excluded.running_run_count,
                                                                             identity_count = excluded.identity_count,
                                                                             active_payload_bytes = excluded.active_payload_bytes,
                                                                             tombstone_logical_bytes = excluded.tombstone_logical_bytes,
                                                                             updated_at_utc = excluded.updated_at_utc;
                                                                         """);
        Add(command, "$accountingVersion", counters.AccountingVersion);
        Add(command, "$nonterminal", counters.NonterminalRunCount);
        Add(command, "$queued", counters.QueuedRunCount);
        Add(command, "$running", counters.RunningRunCount);
        Add(command, "$identities", counters.IdentityCount);
        Add(command, "$activeBytes", counters.ActivePayloadBytes);
        Add(command, "$tombstoneBytes", counters.TombstoneLogicalBytes);
        Add(command, "$updatedAtUtc", counters.UpdatedAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLedgerVersion(McpAgentRunLedgerCounters ledger)
    {
        if (ledger.AccountingVersion != AccountingVersion)
        {
            throw new InvalidOperationException($"Unsupported MCP run accounting version {ledger.AccountingVersion}.");
        }

        if (ledger.NonterminalRunCount < 0 || ledger.QueuedRunCount < 0 || ledger.RunningRunCount < 0
            || ledger.IdentityCount < 0 || ledger.ActivePayloadBytes < 0 || ledger.TombstoneLogicalBytes < 0)
        {
            throw new InvalidOperationException("The MCP run ledger contains a negative counter.");
        }

        if (ledger.NonterminalRunCount != ledger.QueuedRunCount + ledger.RunningRunCount)
        {
            throw new InvalidOperationException("The MCP run ledger nonterminal count does not equal queued plus running.");
        }
    }

    private static McpAgentRunCapacityKind GetCapacityKind(McpAgentRunLedgerCounters ledger, long reservation, long tombstoneBytes)
    {
        if (ledger.NonterminalRunCount >= MaxNonterminalRuns)
        {
            return McpAgentRunCapacityKind.NonterminalRuns;
        }

        if (ledger.IdentityCount >= MaxIdentityCount)
        {
            return McpAgentRunCapacityKind.IdentityCount;
        }

        if (ledger.TombstoneLogicalBytes > MaxTombstoneLogicalBytes - tombstoneBytes)
        {
            return McpAgentRunCapacityKind.TombstoneBytes;
        }

        return ledger.ActivePayloadBytes > MaxActivePayloadBytes - reservation
            ? McpAgentRunCapacityKind.ActivePayloadBytes
            : McpAgentRunCapacityKind.None;
    }
}
