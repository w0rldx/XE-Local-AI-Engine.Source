namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class IntegrationExecutionStore
{
    /// <summary>
    ///     The status literals the count predicates use, derived from the enum so a renamed member breaks the build
    ///     rather than the query. They match the string conversion the entity configuration declares.
    /// </summary>
    private static readonly string ActiveStatusList = string.Join(", ",
        new[]
            {
                IntegrationExecutionStatus.Accepted,
                IntegrationExecutionStatus.Queued,
                IntegrationExecutionStatus.Running
            }
            .Select(static status => $"'{status}'"));

    /// <summary>
    ///     <inheritdoc cref="IIntegrationExecutionStore.AcceptAsync" />
    /// </summary>
    /// <remarks>
    ///     <b>This method deliberately does not use EF.</b> <c>BEGIN IMMEDIATE</c> takes SQLite's write lock at statement one, so a second concurrent accept blocks
    ///     (up to <c>busy_timeout</c>) instead of reading the same count and admitting alongside the first; an EF <c>SaveChanges</c> begins deferred, the racy read
    ///     that would leave the caps bounding nothing under a burst. Nothing it writes is encrypted, which the insert of the accepted event explains.
    /// </remarks>
    public async Task<bool> AcceptAsync(IntegrationAcceptCommand command,
        int maxActive,
        int maxActivePerPrincipal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxActive, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxActivePerPrincipal, other: 1);

        if (command.AcceptedEvent.DetailJson is not null)
        {
            // The premise the encryption skip rests on. Fail loudly rather than write plaintext into an encrypted
            // column.
            throw new ArgumentException("The accepted event must carry no detail: this path does not run the encryption interceptor.", nameof(command));
        }

        // The command carries the same identity twice by design (see IntegrationAcceptCommand), so a caller that disagrees with itself would silently write an
        // execution onto a session, or an event onto an execution, that neither names. Fail before the transaction opens rather than commit an unreachable row.
        if (command.NewSession is { } declaredSession)
        {
            if (declaredSession.SessionId != command.SessionId)
            {
                throw new ArgumentException("The new session's id must equal the command's session id.", nameof(command));
            }

            if (declaredSession.TriggerId != command.TriggerId)
            {
                throw new ArgumentException("The new session's trigger id must equal the command's trigger id.", nameof(command));
            }
        }

        if (command.AcceptedEvent.ExecutionId != command.ExecutionId)
        {
            throw new ArgumentException("The accepted event's execution id must equal the command's execution id.", nameof(command));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = BeginImmediateTransaction(connection);

        // 1. Revocation re-read. Authentication happens before the body is read and before admission does any work, so
        // a credential can be revoked inside that window; this read is what stops it creating durable work.
        var keyState = await ReadKeyRevocationAsync(connection, transaction, command.KeyPrefix, cancellationToken);
        if (keyState is not KeyRevocationState.Live)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        // 2. Node-wide cap, across all triggers and all principals.
        var nodeActive = await CountActiveExecutionsAsync(connection, transaction, principalId: null, cancellationToken);
        if (nodeActive >= maxActive)
        {
            await transaction.CommitAsync(cancellationToken);
            throw new IntegrationQueueFullException("The node's integration execution queue is full.");
        }

        // 3. Per-principal cap. One noisy integrator must not be able to fill the node-wide queue and starve every
        // other principal and the interactive user.
        var principalActive = await CountActiveExecutionsAsync(connection, transaction, command.PrincipalId, cancellationToken);
        if (principalActive >= maxActivePerPrincipal)
        {
            await transaction.CommitAsync(cancellationToken);
            throw new IntegrationQueueFullException("This principal's integration execution queue is full.");
        }

        // 4. The session: a fresh row, or the existing one's counters. Nothing else writes those two columns.
        if (command.NewSession is { } newSession)
        {
            await InsertSessionAsync(connection, transaction, command, newSession, cancellationToken);
        }
        else
        {
            await BumpSessionAsync(connection, transaction, command, cancellationToken);
        }

        // 5 and 6. The execution and its accepted event, then commit.
        await InsertExecutionAsync(connection, transaction, command, cancellationToken);
        await InsertAcceptedEventAsync(connection, transaction, command.AcceptedEvent, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await NodeSqlitePragmas.OpenAndConfigureAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

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

    /// <summary>
    ///     Maps a null to <see cref="DBNull" /> and otherwise binds the value as it is.
    /// </summary>
    /// <remarks>
    ///     A Guid is deliberately handed to the provider unconverted rather than as a "D" string: the rows this path
    ///     writes are read back through EF, so the two Guid encodings have to agree, and the provider's own binding is
    ///     what EF uses. <c>McpAgentRunStore.ToDb</c> differs because every read of its table is raw, so it is free to
    ///     pick its own form.
    /// </remarks>
    private static object ToDb(object? value) =>
        value ?? DBNull.Value;

    private static async Task<KeyRevocationState> ReadKeyRevocationAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        string keyPrefix,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "SELECT revoked_at_utc FROM integration_api_keys WHERE key_prefix = $keyPrefix;");
        Add(command, "$keyPrefix", keyPrefix);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return KeyRevocationState.Missing;
        }

        return await reader.IsDBNullAsync(ordinal: 0, cancellationToken) ? KeyRevocationState.Live : KeyRevocationState.Revoked;
    }

    /// <summary>
    ///     The folded count, and deliberately NOT named <c>CountActiveAsync</c>: that store method was retired when the
    ///     count moved inside this transaction, and a reviewer grepping for the retired name must not find a hit.
    /// </summary>
    private static async Task<long> CountActiveExecutionsAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        Guid? principalId,
        CancellationToken cancellationToken)
    {
        var commandText = principalId is null
            ? $"SELECT COUNT(*) FROM integration_executions WHERE status IN ({ActiveStatusList});"
            : $"SELECT COUNT(*) FROM integration_executions WHERE principal_id = $principalId AND status IN ({ActiveStatusList});";

        await using var command = CreateCommand(connection, transaction, commandText);
        if (principalId is { } principal)
        {
            Add(command, "$principalId", ToDb(principal));
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task InsertSessionAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        IntegrationAcceptCommand command,
        IntegrationSessionCreate newSession,
        CancellationToken cancellationToken)
    {
        await using var insert = CreateCommand(connection, transaction, """
                                                                        INSERT INTO integration_sessions (id, trigger_id, principal_id, conversation_id, agent_definition_id,
                                                                                                          status, created_at_utc, last_activity_utc, execution_count, last_sequence)
                                                                        VALUES ($sessionId, $triggerId, $principalId, $conversationId, $agentDefinitionId,
                                                                                $status, $receivedAtUtc, $receivedAtUtc, 1, $sequence);
                                                                        """);
        Add(insert, "$sessionId", ToDb(newSession.SessionId));
        Add(insert, "$triggerId", ToDb(newSession.TriggerId));
        Add(insert, "$principalId", ToDb(command.PrincipalId));
        Add(insert, "$conversationId", ToDb(newSession.ConversationId));
        Add(insert, "$agentDefinitionId", ToDb(newSession.AgentDefinitionId));
        Add(insert, "$status", IntegrationSessionStatus.Active.ToString());
        Add(insert, "$receivedAtUtc", command.ReceivedAtUtc);
        Add(insert, "$sequence", command.AcceptedEvent.Sequence);
        _ = await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task BumpSessionAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        IntegrationAcceptCommand command,
        CancellationToken cancellationToken)
    {
        await using var update = CreateCommand(connection, transaction, """
                                                                        UPDATE integration_sessions
                                                                           SET execution_count = execution_count + 1, last_activity_utc = $receivedAtUtc
                                                                         WHERE id = $sessionId AND principal_id = $principalId AND status = $status;
                                                                        """);
        Add(update, "$receivedAtUtc", command.ReceivedAtUtc);
        Add(update, "$sessionId", ToDb(command.SessionId));
        Add(update, "$principalId", ToDb(command.PrincipalId));
        Add(update, "$status", IntegrationSessionStatus.Active.ToString());

        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            // Missing, another principal's, or closed. An unscoped UPDATE would silently affect nothing and let the accept commit an execution onto a session that
            // cannot host it; aborting here is what keeps the join checked, and the transaction is disposed uncommitted, so nothing lands in any of the three tables.
            throw new IntegrationSessionUnavailableException("The continuation's session is missing, not this principal's, or no longer active.");
        }
    }

    private static async Task InsertExecutionAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        IntegrationAcceptCommand command,
        CancellationToken cancellationToken)
    {
        await using var insert = CreateCommand(connection, transaction, """
                                                                        INSERT INTO integration_executions (id, trigger_id, session_id, principal_id, request_id, request_fingerprint,
                                                                                                            key_prefix, invocation_id, status, received_at_utc, started_at_utc, ended_at_utc,
                                                                                                            stop_requested_at_utc, failure_category, failure_summary,
                                                                                                            output_count, output_bytes, last_sequence, version)
                                                                        VALUES ($executionId, $triggerId, $sessionId, $principalId, $requestId, $fingerprint,
                                                                                $keyPrefix, $invocationId, $status, $receivedAtUtc, NULL, NULL,
                                                                                NULL, NULL, NULL, 0, 0, $sequence, 0);
                                                                        """);
        Add(insert, "$executionId", ToDb(command.ExecutionId));
        Add(insert, "$triggerId", ToDb(command.TriggerId));
        Add(insert, "$sessionId", ToDb(command.SessionId));
        Add(insert, "$principalId", ToDb(command.PrincipalId));
        Add(insert, "$requestId", ToDb(command.RequestId));
        Add(insert, "$fingerprint", command.RequestFingerprint);
        Add(insert, "$keyPrefix", command.KeyPrefix);
        Add(insert, "$invocationId", ToDb(Guid.Empty));
        Add(insert, "$status", IntegrationExecutionStatus.Accepted.ToString());
        Add(insert, "$receivedAtUtc", command.ReceivedAtUtc);
        Add(insert, "$sequence", command.AcceptedEvent.Sequence);
        _ = await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAcceptedEventAsync(SqliteConnection connection,
        SqliteTransaction transaction,
        IntegrationEventAppend acceptedEvent,
        CancellationToken cancellationToken)
    {
        // A raw-ADO write never runs NodeEncryptionSaveChangesInterceptor, and detail_json is the one encrypted column among the three rows this method writes — the
        // payload-free execution.accepted event leaves it NULL. Give it a payload and seal it here: NodePayloadProtector.Encrypt(…, executionId, eventId, "integration_execution_event_detail_json").
        await using var insert = CreateCommand(connection, transaction, """
                                                                        INSERT INTO integration_execution_events (id, execution_id, sequence, event_type, detail_json, occurred_at_utc)
                                                                        VALUES ($eventId, $executionId, $sequence, $eventType, NULL, $occurredAtUtc);
                                                                        """);
        Add(insert, "$eventId", ToDb(acceptedEvent.EventId));
        Add(insert, "$executionId", ToDb(acceptedEvent.ExecutionId));
        Add(insert, "$sequence", acceptedEvent.Sequence);
        Add(insert, "$eventType", acceptedEvent.EventType);
        Add(insert, "$occurredAtUtc", acceptedEvent.OccurredAtUtc);
        _ = await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private enum KeyRevocationState
    {
        Live,
        Revoked,
        Missing
    }
}
