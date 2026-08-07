namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Durable, bounded persistence contract for inbound MCP agent runs.</summary>
public interface IMcpAgentRunStore
{
    Task<McpAgentRunAdmissionResult> AdmitAsync(McpAgentRunAdmissionRequest request, CancellationToken cancellationToken = default);

    Task<McpAgentRunRecord?> GetAsync(Guid requestId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpAgentRunRecord>> ListAsync(int limit, McpAgentRunStatus? status = null, CancellationToken cancellationToken = default);

    Task<McpAgentRunClaimResult> TryClaimAsync(Guid requestId, long expectedVersion, long claimedAtUtc, CancellationToken cancellationToken = default);

    Task<McpAgentRunStopResult> RequestStopAsync(Guid requestId,
        long expectedVersion,
        McpAgentRunStopReason reason,
        long requestedAtUtc,
        CancellationToken cancellationToken = default);

    Task<bool> TryFinalizeAsync(McpAgentRunFinalization finalization, CancellationToken cancellationToken = default);

    Task<int> ReconcileInterruptedRunsAsync(long completedAtUtc, CancellationToken cancellationToken = default);

    Task<int> CompactExpiredPayloadsAsync(long expiresBeforeUtc, CancellationToken cancellationToken = default);

    Task<McpAgentRunLedgerVerification> VerifyLedgerAsync(CancellationToken cancellationToken = default);

    Task<McpAgentRunLedgerCounters> RebuildLedgerAsync(long updatedAtUtc, CancellationToken cancellationToken = default);

    Task<McpAgentRunLedgerSnapshot> GetLedgerSnapshotAsync(CancellationToken cancellationToken = default);
}

public enum McpAgentRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

public enum McpAgentRunStopReason
{
    None,
    UserCancellation,
    WatchdogExpired,
    HostShutdown
}

public enum McpAgentRunAdmissionKind
{
    Accepted,
    Existing,
    ResultExpired,
    RequestIdConflict,
    CapacityExceeded
}

public enum McpAgentRunCapacityKind
{
    None,
    NonterminalRuns,
    IdentityCount,
    TombstoneBytes,
    ActivePayloadBytes
}

public enum McpAgentRunClaimKind
{
    Claimed,
    NotFound,
    VersionConflict,
    NotQueued
}

public enum McpAgentRunStopKind
{
    Requested,
    AlreadyRequested,
    AlreadyTerminal,
    NotFound,
    VersionConflict
}

/// <summary>
///     Admission input. <c>CanonicalRequest</c> is the permanent 32-byte keyed fingerprint produced by
///     <c>McpAgentRunPayloadProtector.ComputeRequestFingerprint</c>; the canonical plaintext is never persisted.
/// </summary>
public sealed record McpAgentRunAdmissionRequest(
    Guid RequestId,
    ReadOnlyMemory<byte> CanonicalRequest,
    string Task,
    string? Instructions,
    Guid? AgentDefinitionId,
    long? AgentDefinitionVersion,
    string ModelId,
    string? ModelOverrideId,
    Guid? WorkspaceId,
    ReadOnlyMemory<byte> BindingFingerprint,
    long CreatedAtUtc);

public sealed record McpAgentRunAdmissionResult(
    McpAgentRunAdmissionKind Kind,
    McpAgentRunRecord? Run,
    McpAgentRunCapacityKind CapacityKind = McpAgentRunCapacityKind.None);

public sealed record McpAgentRunClaimResult(McpAgentRunClaimKind Kind, McpAgentRunRecord? Run);

public sealed record McpAgentRunStopResult(McpAgentRunStopKind Kind, McpAgentRunRecord? Run);

public sealed record McpAgentRunFinalization(
    Guid RequestId,
    long ExpectedVersion,
    Guid ClaimToken,
    McpAgentRunStatus Status,
    McpAgentRunStopReason ExpectedStopReason,
    string? FailureCode,
    string? Result,
    string? DisplayMessage,
    long CompletedAtUtc);

public sealed record McpAgentRunRecord(
    Guid RequestId,
    ReadOnlyMemory<byte> RequestFingerprint,
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
    ReadOnlyMemory<byte>? BindingFingerprint,
    string? Task,
    string? Instructions,
    string? Result,
    string? DisplayMessage,
    string? FailureCode,
    long CreatedAtUtc,
    long? ClaimedAtUtc,
    long? CompletedAtUtc,
    long? PayloadExpiresAtUtc,
    long? CompactedAtUtc,
    bool PayloadExpired);

public sealed record McpAgentRunLedgerCounters(
    int AccountingVersion,
    long NonterminalRunCount,
    long QueuedRunCount,
    long RunningRunCount,
    long IdentityCount,
    long ActivePayloadBytes,
    long TombstoneLogicalBytes,
    long UpdatedAtUtc);

public sealed record McpAgentRunLedgerVerification(bool IsConsistent,
    McpAgentRunLedgerCounters Persisted,
    McpAgentRunLedgerCounters Reconstructed);

public sealed record McpAgentRunLedgerSnapshot(long QueueDepth,
    long RunningCount,
    McpAgentRunLedgerCounters Counters);
