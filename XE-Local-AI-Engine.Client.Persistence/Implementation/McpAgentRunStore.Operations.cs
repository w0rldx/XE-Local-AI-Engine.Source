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

}
