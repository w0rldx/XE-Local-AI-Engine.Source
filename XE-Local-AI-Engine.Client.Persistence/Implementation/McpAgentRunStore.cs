namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>SQLite-serialized durable ledger for inbound MCP runs.</summary>
public sealed class McpAgentRunStore : IMcpAgentRunStore
{
    public const int AccountingVersion = 1;
    public const int MaxTaskUtf8Bytes = 32 * 1024;
    public const int MaxInstructionsUtf8Bytes = 16 * 1024;
    public const int MaxResultCharacters = 24_000;
    public const int MaxDisplayUtf8Bytes = 2 * 1024;
    public const long PayloadRetentionMilliseconds = 24L * 60 * 60 * 1000;

    public const int TombstoneReservationBytesV1 = McpAgentRunPayloadProtector.FixedRecordOverheadBytes
                                                   + 16 // request id
                                                   + 32 // keyed request fingerprint
                                                   + 4 // accounting version
                                                   + 4 // terminal status
                                                   + 8 // version
                                                   + 128 // maximum safe ASCII failure code
                                                   + 8 // accepted timestamp
                                                   + 8 // terminal timestamp
                                                   + 8 // compaction timestamp
                                                   + 8; // persisted tombstone logical-byte charge

    public const int MaxNonterminalRuns = 32;
    public const long MaxIdentityCount = 1_000_000;
    public const long MaxActivePayloadBytes = 256L * 1024 * 1024;
    public const long MaxTombstoneLogicalBytes = 128L * 1024 * 1024;

    private readonly string _connectionString;
    private readonly McpAgentRunPayloadProtector _protector;

    public McpAgentRunStore(NodeChatDbContext dbContext, McpAgentRunPayloadProtector protector)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _connectionString = dbContext.Database.GetConnectionString()
                            ?? throw new InvalidOperationException("The MCP run store requires a configured SQLite connection string.");
    }

    public async Task<McpAgentRunAdmissionResult> AdmitAsync(McpAgentRunAdmissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdmission(request);

        var task = Encoding.UTF8.GetBytes(request.Task);
        var instructions = request.Instructions is null ? null : Encoding.UTF8.GetBytes(request.Instructions);
        var fingerprint = request.CanonicalRequest.ToArray();
        var reservation = checked((long)task.Length
                                  + (instructions?.Length ?? 0)
                                  + ((long)MaxResultCharacters * 4)
                                  + MaxDisplayUtf8Bytes
                                  + (4L * _protector.FixedEnvelopeOverheadBytes)
                                  + McpAgentRunPayloadProtector.FixedRecordOverheadBytes);
        const long tombstoneBytes = TombstoneReservationBytesV1;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = BeginImmediateTransaction(connection);
        var existing = await ReadRunAsync(connection, transaction, request.RequestId, includePayload: true, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(fingerprint, existing.RequestFingerprint))
            {
                return new McpAgentRunAdmissionResult(McpAgentRunAdmissionKind.RequestIdConflict, ToRecord(existing));
            }

            return new McpAgentRunAdmissionResult(existing.TaskPayload is null ? McpAgentRunAdmissionKind.ResultExpired : McpAgentRunAdmissionKind.Existing,
                ToRecord(existing));
        }

        var ledger = await LoadRequiredLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var capacityKind = GetCapacityKind(ledger, reservation, tombstoneBytes);
        if (capacityKind != McpAgentRunCapacityKind.None)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new McpAgentRunAdmissionResult(McpAgentRunAdmissionKind.CapacityExceeded, Run: null, CapacityKind: capacityKind);
        }

        var taskPayload = _protector.Protect(request.RequestId, "task", task);
        var instructionsPayload = instructions is null ? null : _protector.Protect(request.RequestId, "instructions", instructions);

        await using (var command = CreateCommand(connection, transaction, """
                                                                          INSERT INTO mcp_agent_runs (
                                                                              request_id, request_fingerprint, accounting_version, status, version, claim_token, stop_reason,
                                                                              stop_requested_at_utc, agent_definition_id, agent_definition_version, model_id, model_override_id, workspace_id,
                                                                              is_agentic_auto_approve, requesting_key_prefix, binding_fingerprint,
                                                                              task_payload, instructions_payload, result_payload, display_payload, failure_code,
                                                                              reserved_active_payload_bytes, active_payload_bytes, tombstone_logical_bytes, created_at_utc,
                                                                              claimed_at_utc, completed_at_utc, payload_expires_at_utc, compacted_at_utc)
                                                                          VALUES ($requestId, $fingerprint, $accountingVersion, $status, 0, NULL, $stopReason, NULL,
                                                                              $agentDefinitionId, $agentDefinitionVersion, $modelId, $modelOverrideId, $workspaceId,
                                                                              $isAgenticAutoApprove, $requestingKeyPrefix, $bindingFingerprint,
                                                                              $taskPayload, $instructionsPayload, NULL, NULL, NULL, $reservation, $reservation, $tombstoneBytes,
                                                                              $createdAtUtc, NULL, NULL, NULL, NULL);
                                                                          """))
        {
            Add(command, "$requestId", request.RequestId.ToString("D", CultureInfo.InvariantCulture));
            Add(command, "$fingerprint", fingerprint);
            Add(command, "$accountingVersion", AccountingVersion);
            Add(command, "$status", (int)McpAgentRunStatus.Queued);
            Add(command, "$stopReason", (int)McpAgentRunStopReason.None);
            Add(command, "$agentDefinitionId", ToDb(request.AgentDefinitionId));
            Add(command, "$agentDefinitionVersion", ToDb(request.AgentDefinitionVersion));
            Add(command, "$modelId", request.ModelId);
            Add(command, "$modelOverrideId", ToDb(request.ModelOverrideId));
            Add(command, "$workspaceId", ToDb(request.WorkspaceId));
            Add(command, "$isAgenticAutoApprove", request.IsAgenticAutoApprove ? 1 : 0);
            Add(command, "$requestingKeyPrefix", ToDb(request.RequestingKeyPrefix));
            Add(command, "$bindingFingerprint", request.BindingFingerprint);
            Add(command, "$taskPayload", taskPayload);
            Add(command, "$instructionsPayload", ToDb(instructionsPayload));
            Add(command, "$reservation", reservation);
            Add(command, "$tombstoneBytes", tombstoneBytes);
            Add(command, "$createdAtUtc", request.CreatedAtUtc);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateLedgerAsync(connection,
            transaction,
            ledger with
            {
                NonterminalRunCount = ledger.NonterminalRunCount + 1,
                QueuedRunCount = ledger.QueuedRunCount + 1,
                IdentityCount = ledger.IdentityCount + 1,
                ActivePayloadBytes = ledger.ActivePayloadBytes + reservation,
                TombstoneLogicalBytes = ledger.TombstoneLogicalBytes + tombstoneBytes,
                UpdatedAtUtc = request.CreatedAtUtc
            },
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new McpAgentRunAdmissionResult(McpAgentRunAdmissionKind.Accepted,
            new McpAgentRunRecord(request.RequestId,
                fingerprint.ToArray(),
                McpAgentRunStatus.Queued,
                Version: 0,
                ClaimToken: null,
                McpAgentRunStopReason.None,
                StopRequestedAtUtc: null,
                request.AgentDefinitionId,
                request.AgentDefinitionVersion,
                request.ModelId,
                request.ModelOverrideId,
                request.WorkspaceId,
                request.BindingFingerprint.ToArray(),
                request.Task,
                request.Instructions,
                Result: null,
                DisplayMessage: null,
                FailureCode: null,
                request.CreatedAtUtc,
                ClaimedAtUtc: null,
                CompletedAtUtc: null,
                PayloadExpiresAtUtc: null,
                CompactedAtUtc: null,
                PayloadExpired: false,
                IsAgenticAutoApprove: request.IsAgenticAutoApprove,
                RequestingKeyPrefix: request.RequestingKeyPrefix));
    }

    public async Task<McpAgentRunRecord?> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadRunAsync(connection, transaction: null, requestId, includePayload: true, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<McpAgentRunRecord>> ListAsync(int limit,
        McpAgentRunStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The list limit must be between 1 and 50.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = MetadataSelectColumns + (status is null ? string.Empty : " WHERE status = $status") + " ORDER BY created_at_utc DESC LIMIT $limit;";
        await using var command = CreateCommand(connection, transaction: null, sql);
        if (status is not null)
        {
            Add(command, "$status", (int)status.Value);
        }

        Add(command, "$limit", limit);
        var rows = new List<McpAgentRunRecord>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ToRecord(ReadRow(reader, includePayload: false)));
        }

        return rows;
    }

    public async Task<McpAgentRunClaimResult> TryClaimAsync(Guid requestId,
        long expectedVersion,
        long claimedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = BeginImmediateTransaction(connection);
        var ledger = await LoadRequiredLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var row = await ReadRunAsync(connection, transaction, requestId, includePayload: true, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new McpAgentRunClaimResult(McpAgentRunClaimKind.NotFound, Run: null);
        }

        if (row.Version != expectedVersion)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new McpAgentRunClaimResult(McpAgentRunClaimKind.VersionConflict, ToRecord(row));
        }

        if (row.Status != McpAgentRunStatus.Queued || row.StopReason != McpAgentRunStopReason.None)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new McpAgentRunClaimResult(McpAgentRunClaimKind.NotQueued, ToRecord(row));
        }

        var token = Guid.NewGuid();
        await using var command = CreateCommand(connection, transaction, """
                                                                         UPDATE mcp_agent_runs
                                                                         SET status = $running, version = version + 1, claim_token = $claimToken, claimed_at_utc = $claimedAtUtc
                                                                         WHERE request_id = $requestId AND version = $expectedVersion AND status = $queued AND stop_reason = $none;
                                                                         """);
        Add(command, "$running", (int)McpAgentRunStatus.Running);
        Add(command, "$claimToken", token.ToString("D", CultureInfo.InvariantCulture));
        Add(command, "$claimedAtUtc", claimedAtUtc);
        Add(command, "$requestId", requestId.ToString("D", CultureInfo.InvariantCulture));
        Add(command, "$expectedVersion", expectedVersion);
        Add(command, "$queued", (int)McpAgentRunStatus.Queued);
        Add(command, "$none", (int)McpAgentRunStopReason.None);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException("The serialized MCP run claim unexpectedly lost its compare-and-swap.");
        }

        await UpdateLedgerAsync(connection,
            transaction,
            ledger with
            {
                QueuedRunCount = ledger.QueuedRunCount - 1,
                RunningRunCount = ledger.RunningRunCount + 1,
                UpdatedAtUtc = claimedAtUtc
            },
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new McpAgentRunClaimResult(McpAgentRunClaimKind.Claimed,
            ToRecord(row with
            {
                Status = McpAgentRunStatus.Running,
                Version = row.Version + 1,
                ClaimToken = token,
                ClaimedAtUtc = claimedAtUtc
            }));
    }

    public async Task<McpAgentRunStopResult> RequestStopAsync(Guid requestId,
        long expectedVersion,
        McpAgentRunStopReason reason,
        long requestedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (reason == McpAgentRunStopReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = BeginImmediateTransaction(connection);
        var ledger = await LoadRequiredLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var row = await ReadRunAsync(connection, transaction, requestId, includePayload: true, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new McpAgentRunStopResult(McpAgentRunStopKind.NotFound, Run: null);
        }

        if (IsTerminal(row.Status))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new McpAgentRunStopResult(McpAgentRunStopKind.AlreadyTerminal, ToRecord(row));
        }

        if (row.StopReason != McpAgentRunStopReason.None)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new McpAgentRunStopResult(McpAgentRunStopKind.AlreadyRequested, ToRecord(row));
        }

        if (row.Version != expectedVersion)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new McpAgentRunStopResult(McpAgentRunStopKind.VersionConflict, ToRecord(row));
        }

        var queued = row.Status == McpAgentRunStatus.Queued;
        var status = queued ? StatusForStop(reason) : row.Status;
        var failureCode = queued ? FailureCodeForStop(reason) : row.FailureCode;
        var activePayloadBytes = queued ? CalculateStoredPayloadBytes(row) : row.ActivePayloadBytes;
        var payloadExpiresAtUtc = queued ? checked(requestedAtUtc + PayloadRetentionMilliseconds) : row.PayloadExpiresAtUtc;
        await using (var command = CreateCommand(connection, transaction, """
                                                                          UPDATE mcp_agent_runs
                                                                          SET status = $status, version = version + 1, stop_reason = $stopReason,
                                                                              stop_requested_at_utc = $requestedAtUtc, completed_at_utc = $completedAtUtc,
                                                                              active_payload_bytes = $activePayloadBytes, failure_code = $failureCode,
                                                                              payload_expires_at_utc = $payloadExpiresAtUtc
                                                                          WHERE request_id = $requestId AND version = $expectedVersion AND stop_reason = $none;
                                                                          """))
        {
            Add(command, "$status", (int)status);
            Add(command, "$stopReason", (int)reason);
            Add(command, "$requestedAtUtc", requestedAtUtc);
            Add(command, "$completedAtUtc", queued ? requestedAtUtc : DBNull.Value);
            Add(command, "$activePayloadBytes", activePayloadBytes);
            Add(command, "$failureCode", ToDb(failureCode));
            Add(command, "$payloadExpiresAtUtc", ToDb(payloadExpiresAtUtc));
            Add(command, "$requestId", requestId.ToString("D", CultureInfo.InvariantCulture));
            Add(command, "$expectedVersion", expectedVersion);
            Add(command, "$none", (int)McpAgentRunStopReason.None);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The serialized MCP stop request unexpectedly lost its compare-and-swap.");
            }
        }

        if (queued)
        {
            await UpdateLedgerAsync(connection,
                transaction,
                ledger with
                {
                    NonterminalRunCount = ledger.NonterminalRunCount - 1,
                    QueuedRunCount = ledger.QueuedRunCount - 1,
                    ActivePayloadBytes = ledger.ActivePayloadBytes + activePayloadBytes - row.ActivePayloadBytes,
                    UpdatedAtUtc = requestedAtUtc
                },
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new McpAgentRunStopResult(McpAgentRunStopKind.Requested,
            ToRecord(row with
            {
                Status = status,
                Version = row.Version + 1,
                StopReason = reason,
                StopRequestedAtUtc = requestedAtUtc,
                CompletedAtUtc = queued ? requestedAtUtc : null,
                FailureCode = failureCode,
                ActivePayloadBytes = activePayloadBytes,
                PayloadExpiresAtUtc = payloadExpiresAtUtc
            }));
    }

    public async Task<bool> TryFinalizeAsync(McpAgentRunFinalization finalization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalization);
        ValidateFinalization(finalization);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = BeginImmediateTransaction(connection);
        var ledger = await LoadRequiredLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var row = await ReadRunAsync(connection, transaction, finalization.RequestId, includePayload: true, cancellationToken).ConfigureAwait(false);
        if (row is null
            || row.Status != McpAgentRunStatus.Running
            || row.Version != finalization.ExpectedVersion
            || row.ClaimToken != finalization.ClaimToken
            || row.StopReason != finalization.ExpectedStopReason)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var resultPayload = finalization.Result is null
            ? null
            : _protector.Protect(finalization.RequestId, "result", Encoding.UTF8.GetBytes(finalization.Result));
        var displayPayload = finalization.DisplayMessage is null
            ? null
            : _protector.Protect(finalization.RequestId, "display", Encoding.UTF8.GetBytes(finalization.DisplayMessage));
        var actualActiveBytes = checked((long)(row.TaskPayload?.Length ?? 0)
                                        + (row.InstructionsPayload?.Length ?? 0)
                                        + (resultPayload?.Length ?? 0)
                                        + (displayPayload?.Length ?? 0)
                                        + McpAgentRunPayloadProtector.FixedRecordOverheadBytes);
        var payloadExpiresAtUtc = checked(finalization.CompletedAtUtc + PayloadRetentionMilliseconds);

        await using (var command = CreateCommand(connection, transaction, """
                                                                          UPDATE mcp_agent_runs
                                                                          SET status = $status, version = version + 1, result_payload = $resultPayload,
                                                                              display_payload = $displayPayload, failure_code = $failureCode, completed_at_utc = $completedAtUtc,
                                                                              active_payload_bytes = $actualActiveBytes, payload_expires_at_utc = $payloadExpiresAtUtc
                                                                          WHERE request_id = $requestId AND version = $expectedVersion AND status = $running
                                                                              AND claim_token = $claimToken AND stop_reason = $expectedStopReason;
                                                                          """))
        {
            Add(command, "$status", (int)finalization.Status);
            Add(command, "$resultPayload", ToDb(resultPayload));
            Add(command, "$displayPayload", ToDb(displayPayload));
            Add(command, "$failureCode", ToDb(finalization.FailureCode));
            Add(command, "$completedAtUtc", finalization.CompletedAtUtc);
            Add(command, "$actualActiveBytes", actualActiveBytes);
            Add(command, "$payloadExpiresAtUtc", payloadExpiresAtUtc);
            Add(command, "$requestId", finalization.RequestId.ToString("D", CultureInfo.InvariantCulture));
            Add(command, "$expectedVersion", finalization.ExpectedVersion);
            Add(command, "$running", (int)McpAgentRunStatus.Running);
            Add(command, "$claimToken", finalization.ClaimToken.ToString("D", CultureInfo.InvariantCulture));
            Add(command, "$expectedStopReason", (int)finalization.ExpectedStopReason);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The serialized MCP terminal transition unexpectedly lost its compare-and-swap.");
            }
        }

        await UpdateLedgerAsync(connection,
            transaction,
            ledger with
            {
                NonterminalRunCount = ledger.NonterminalRunCount - 1,
                RunningRunCount = ledger.RunningRunCount - 1,
                ActivePayloadBytes = checked(ledger.ActivePayloadBytes + actualActiveBytes - row.ActivePayloadBytes),
                UpdatedAtUtc = finalization.CompletedAtUtc
            },
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> ReconcileInterruptedRunsAsync(long completedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = BeginImmediateTransaction(connection);
        var ledger = await LoadRequiredLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        long releasedBytes;
        await using (var totals = CreateCommand(connection, transaction, """
                                                                         SELECT COALESCE(SUM(active_payload_bytes - (
                                                                             COALESCE(length(task_payload), 0) + COALESCE(length(instructions_payload), 0)
                                                                             + COALESCE(length(result_payload), 0) + COALESCE(length(display_payload), 0) + $recordOverhead)), 0)
                                                                         FROM mcp_agent_runs WHERE status = $running;
                                                                         """))
        {
            Add(totals, "$recordOverhead", McpAgentRunPayloadProtector.FixedRecordOverheadBytes);
            Add(totals, "$running", (int)McpAgentRunStatus.Running);
            releasedBytes = Convert.ToInt64(await totals.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        if (releasedBytes < 0)
        {
            throw new InvalidOperationException("MCP run payload accounting is smaller than the persisted encrypted envelopes.");
        }

        await using var command = CreateCommand(connection, transaction, """
                                                                         UPDATE mcp_agent_runs
                                                                         SET status = CASE stop_reason
                                                                                 WHEN $userCancellation THEN $cancelled
                                                                                 WHEN $watchdogExpired THEN $failed
                                                                                 ELSE $interrupted
                                                                             END,
                                                                             version = version + 1,
                                                                             completed_at_utc = $completedAtUtc,
                                                                             payload_expires_at_utc = $payloadExpiresAtUtc,
                                                                             active_payload_bytes = COALESCE(length(task_payload), 0) + COALESCE(length(instructions_payload), 0)
                                                                                 + COALESCE(length(result_payload), 0) + COALESCE(length(display_payload), 0) + $recordOverhead,
                                                                             failure_code = CASE stop_reason
                                                                                 WHEN $userCancellation THEN 'cancelled'
                                                                                 WHEN $watchdogExpired THEN 'watchdog_expired'
                                                                                 ELSE 'interrupted'
                                                                             END
                                                                         WHERE status = $running;
                                                                         """);
        Add(command, "$interrupted", (int)McpAgentRunStatus.Interrupted);
        Add(command, "$cancelled", (int)McpAgentRunStatus.Cancelled);
        Add(command, "$failed", (int)McpAgentRunStatus.Failed);
        Add(command, "$userCancellation", (int)McpAgentRunStopReason.UserCancellation);
        Add(command, "$watchdogExpired", (int)McpAgentRunStopReason.WatchdogExpired);
        Add(command, "$completedAtUtc", completedAtUtc);
        Add(command, "$payloadExpiresAtUtc", checked(completedAtUtc + PayloadRetentionMilliseconds));
        Add(command, "$recordOverhead", McpAgentRunPayloadProtector.FixedRecordOverheadBytes);
        Add(command, "$running", (int)McpAgentRunStatus.Running);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed > 0)
        {
            await UpdateLedgerAsync(connection,
                transaction,
                ledger with
                {
                    NonterminalRunCount = ledger.NonterminalRunCount - changed,
                    RunningRunCount = ledger.RunningRunCount - changed,
                    ActivePayloadBytes = ledger.ActivePayloadBytes - releasedBytes,
                    UpdatedAtUtc = completedAtUtc
                },
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<int> CompactExpiredPayloadsAsync(long expiresBeforeUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = BeginImmediateTransaction(connection);
        var ledger = await LoadRequiredLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        long released;
        int count;
        await using (var totals = CreateCommand(connection, transaction, """
                                                                         SELECT COUNT(*), COALESCE(SUM(active_payload_bytes), 0)
                                                                         FROM mcp_agent_runs
                                                                         WHERE status IN ($succeeded, $failed, $cancelled, $interrupted)
                                                                             AND payload_expires_at_utc IS NOT NULL AND payload_expires_at_utc <= $cutoff AND active_payload_bytes > 0;
                                                                         """))
        {
            Add(totals, "$succeeded", (int)McpAgentRunStatus.Succeeded);
            Add(totals, "$failed", (int)McpAgentRunStatus.Failed);
            Add(totals, "$cancelled", (int)McpAgentRunStatus.Cancelled);
            Add(totals, "$interrupted", (int)McpAgentRunStatus.Interrupted);
            Add(totals, "$cutoff", expiresBeforeUtc);
            await using var reader = await totals.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            count = checked((int)reader.GetInt64(0));
            released = reader.GetInt64(1);
        }

        if (count > 0)
        {
            await using var compact = CreateCommand(connection, transaction, """
                                                                             UPDATE mcp_agent_runs
                                                                             SET version = version + 1,
                                                                                 claim_token = NULL,
                                                                                 stop_reason = $none,
                                                                                 stop_requested_at_utc = NULL,
                                                                                 agent_definition_id = NULL,
                                                                                 agent_definition_version = NULL,
                                                                                 model_id = NULL,
                                                                                 model_override_id = NULL,
                                                                                 workspace_id = NULL,
                                                                                 binding_fingerprint = NULL,
                                                                                 task_payload = NULL,
                                                                                 instructions_payload = NULL,
                                                                                 result_payload = NULL,
                                                                                 display_payload = NULL,
                                                                                 reserved_active_payload_bytes = 0,
                                                                                 active_payload_bytes = 0,
                                                                                 claimed_at_utc = NULL,
                                                                                 payload_expires_at_utc = NULL,
                                                                                 compacted_at_utc = $compactedAtUtc
                                                                             WHERE status IN ($succeeded, $failed, $cancelled, $interrupted)
                                                                                 AND payload_expires_at_utc IS NOT NULL AND payload_expires_at_utc <= $cutoff AND active_payload_bytes > 0;
                                                                             """);
            Add(compact, "$succeeded", (int)McpAgentRunStatus.Succeeded);
            Add(compact, "$failed", (int)McpAgentRunStatus.Failed);
            Add(compact, "$cancelled", (int)McpAgentRunStatus.Cancelled);
            Add(compact, "$interrupted", (int)McpAgentRunStatus.Interrupted);
            Add(compact, "$none", (int)McpAgentRunStopReason.None);
            Add(compact, "$cutoff", expiresBeforeUtc);
            Add(compact, "$compactedAtUtc", expiresBeforeUtc);
            _ = await compact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await UpdateLedgerAsync(connection,
                transaction,
                ledger with
                {
                    ActivePayloadBytes = ledger.ActivePayloadBytes - released,
                    UpdatedAtUtc = expiresBeforeUtc
                },
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task<McpAgentRunLedgerVerification> VerifyLedgerAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = BeginImmediateTransaction(connection);
        var persisted = await LoadRequiredLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var reconstructed = await ReconstructCountersAsync(connection, transaction, persisted.UpdatedAtUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new McpAgentRunLedgerVerification(CountersEqual(persisted, reconstructed), persisted, reconstructed);
    }

    public async Task<McpAgentRunLedgerCounters> RebuildLedgerAsync(long updatedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = BeginImmediateTransaction(connection);
        var counters = await ReconstructCountersAsync(connection, transaction, updatedAtUtc, cancellationToken).ConfigureAwait(false);
        await UpsertLedgerAsync(connection, transaction, counters, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counters;
    }

    public async Task<McpAgentRunLedgerSnapshot> GetLedgerSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var counters = await LoadRequiredLedgerAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new McpAgentRunLedgerSnapshot(counters.QueuedRunCount, counters.RunningRunCount, counters);
    }

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

    private static void ValidateAdmission(McpAgentRunAdmissionRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty request id is required.", nameof(request));
        }

        if (request.CanonicalRequest.Length != 32)
        {
            throw new ArgumentException("CanonicalRequest must contain the 32-byte keyed request fingerprint.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Task) || Encoding.UTF8.GetByteCount(request.Task) > MaxTaskUtf8Bytes)
        {
            throw new ArgumentException("The task must be non-empty and at most 32 KiB of UTF-8.", nameof(request));
        }

        if (request.Instructions is not null && Encoding.UTF8.GetByteCount(request.Instructions) > MaxInstructionsUtf8Bytes)
        {
            throw new ArgumentException("Instructions must be at most 16 KiB of UTF-8.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ModelId) || request.ModelId.Length > 1024 || ContainsLineBreak(request.ModelId))
        {
            throw new ArgumentException("A stable single-line model id is required.", nameof(request));
        }

        if (request.ModelOverrideId is not null
            && (string.IsNullOrWhiteSpace(request.ModelOverrideId) || request.ModelOverrideId.Length > 1024 || ContainsLineBreak(request.ModelOverrideId)))
        {
            throw new ArgumentException("The model override id must be stable and single-line.", nameof(request));
        }

        if (request.BindingFingerprint.Length != 32)
        {
            throw new ArgumentException("The binding fingerprint must be 32 bytes.", nameof(request));
        }

        if ((!request.IsAgenticAutoApprove && request.RequestingKeyPrefix is not null)
            || (request.IsAgenticAutoApprove && !IsBoundedKeyPrefix(request.RequestingKeyPrefix)))
        {
            throw new ArgumentException("Agentic authority and its bounded ASCII requesting key prefix must be present together.", nameof(request));
        }
    }

    private static bool IsBoundedKeyPrefix(string? value)
    {
        return value is { Length: >= 1 and <= 32 }
               && value.All(static character => character is >= 'a' and <= 'z'
                   or >= 'A' and <= 'Z'
                   or >= '0' and <= '9'
                   or '_' or '-');
    }

    private static void ValidateFinalization(McpAgentRunFinalization finalization)
    {
        if (!IsTerminal(finalization.Status))
        {
            throw new ArgumentOutOfRangeException(nameof(finalization), "Finalization requires a terminal status.");
        }

        if (finalization.Result is { Length: > MaxResultCharacters })
        {
            throw new ArgumentException("The result exceeds the 24,000 character limit.", nameof(finalization));
        }

        if (finalization.DisplayMessage is not null && Encoding.UTF8.GetByteCount(finalization.DisplayMessage) > MaxDisplayUtf8Bytes)
        {
            throw new ArgumentException("The display message exceeds the 2 KiB UTF-8 limit.", nameof(finalization));
        }

        if (finalization.FailureCode is { Length: > 128 } || finalization.FailureCode is not null && !IsSafeCode(finalization.FailureCode))
        {
            throw new ArgumentException("The failure code contains unsupported characters.", nameof(finalization));
        }

        if (finalization.ExpectedStopReason == McpAgentRunStopReason.None && finalization.Status is McpAgentRunStatus.Cancelled or McpAgentRunStatus.Interrupted)
        {
            throw new ArgumentException("Cancelled and interrupted finalizations require a stop marker.", nameof(finalization));
        }

        if (finalization.ExpectedStopReason != McpAgentRunStopReason.None && finalization.Status != StatusForStop(finalization.ExpectedStopReason))
        {
            throw new ArgumentException("The terminal status does not match the persisted stop reason.", nameof(finalization));
        }
    }

    private static bool IsSafeCode(string value)
    {
        return value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    }

    private static bool ContainsLineBreak(string value) =>
        value.AsSpan().Contains('\r') || value.AsSpan().Contains('\n');

    private static bool IsTerminal(McpAgentRunStatus status) =>
        status is McpAgentRunStatus.Succeeded
            or McpAgentRunStatus.Failed
            or McpAgentRunStatus.Cancelled
            or McpAgentRunStatus.Interrupted;

    private static McpAgentRunStatus StatusForStop(McpAgentRunStopReason reason) =>
        reason switch
        {
            McpAgentRunStopReason.UserCancellation => McpAgentRunStatus.Cancelled,
            McpAgentRunStopReason.WatchdogExpired => McpAgentRunStatus.Failed,
            McpAgentRunStopReason.HostShutdown => McpAgentRunStatus.Interrupted,
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

    private static string FailureCodeForStop(McpAgentRunStopReason reason) =>
        reason switch
        {
            McpAgentRunStopReason.UserCancellation => "cancelled",
            McpAgentRunStopReason.WatchdogExpired => "watchdog_expired",
            McpAgentRunStopReason.HostShutdown => "interrupted",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite has no async transaction overload that preserves BEGIN IMMEDIATE serialization.")]
    private static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection) =>
        connection.BeginTransaction(deferred: false);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Command text is assembled exclusively from private fixed SQL fragments; all runtime values are bound parameters.")]
    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object value)
    {
        var providerValue = value is ReadOnlyMemory<byte> buffer ? buffer.ToArray() : value;
        _ = command.Parameters.AddWithValue(name, providerValue);
    }

    private static object ToDb(object? value) =>
        value switch
        {
            null => DBNull.Value,
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            _ => value
        };

    private static Guid? GetNullableGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Guid.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static byte[]? GetNullableBytes(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);

    private sealed record McpAgentRunRow(
        Guid RequestId,
        byte[] RequestFingerprint,
        int StoredAccountingVersion,
        McpAgentRunStatus Status,
        long Version,
        Guid? ClaimToken,
        McpAgentRunStopReason StopReason,
        long? StopRequestedAtUtc,
        Guid? AgentDefinitionId,
        long? AgentDefinitionVersion,
        string? ModelId,
        string? ModelOverrideId,
        Guid? WorkspaceId,
        bool IsAgenticAutoApprove,
        string? RequestingKeyPrefix,
        byte[]? BindingFingerprint,
        byte[]? TaskPayload,
        byte[]? InstructionsPayload,
        byte[]? ResultPayload,
        byte[]? DisplayPayload,
        string? FailureCode,
        long ReservedActivePayloadBytes,
        long ActivePayloadBytes,
        long TombstoneLogicalBytes,
        long CreatedAtUtc,
        long? ClaimedAtUtc,
        long? CompletedAtUtc,
        long? PayloadExpiresAtUtc,
        long? CompactedAtUtc);
}
