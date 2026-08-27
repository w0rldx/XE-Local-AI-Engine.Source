namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class McpAgentRunStore
{
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
