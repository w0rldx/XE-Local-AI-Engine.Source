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
public sealed record McpAgentRunAdmissionRequest
{
    public required Guid RequestId { get; init; }

    public required ReadOnlyMemory<byte> CanonicalRequest { get; init; }

    public required string Task { get; init; }

    public required string? Instructions { get; init; }

    public required Guid? AgentDefinitionId { get; init; }

    public required long? AgentDefinitionVersion { get; init; }

    public required string ModelId { get; init; }

    public required string? ModelOverrideId { get; init; }

    public required Guid? WorkspaceId { get; init; }

    public required ReadOnlyMemory<byte> BindingFingerprint { get; init; }

    public required long CreatedAtUtc { get; init; }

    public bool IsAgenticAutoApprove { get; init; }

    public string? RequestingKeyPrefix { get; init; }
}

public sealed class McpAgentRunAdmissionResult
{
    public required McpAgentRunAdmissionKind Kind { get; init; }

    public required McpAgentRunRecord? Run { get; init; }

    public McpAgentRunCapacityKind CapacityKind { get; init; }
}

public sealed class McpAgentRunClaimResult
{
    public required McpAgentRunClaimKind Kind { get; init; }

    public required McpAgentRunRecord? Run { get; init; }
}

public sealed class McpAgentRunStopResult
{
    public required McpAgentRunStopKind Kind { get; init; }

    public required McpAgentRunRecord? Run { get; init; }
}

public sealed record McpAgentRunFinalization
{
    public required Guid RequestId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid ClaimToken { get; init; }

    public required McpAgentRunStatus Status { get; init; }

    public required McpAgentRunStopReason ExpectedStopReason { get; init; }

    public required string? FailureCode { get; init; }

    public required string? Result { get; init; }

    public required string? DisplayMessage { get; init; }

    public required long CompletedAtUtc { get; init; }
}

public sealed record McpAgentRunRecord
{
    public required Guid RequestId { get; init; }

    public required ReadOnlyMemory<byte> RequestFingerprint { get; init; }

    public required McpAgentRunStatus Status { get; init; }

    public required long Version { get; init; }

    public required Guid? ClaimToken { get; init; }

    public required McpAgentRunStopReason StopReason { get; init; }

    public required long? StopRequestedAtUtc { get; init; }

    public required Guid? AgentDefinitionId { get; init; }

    public required long? AgentDefinitionVersion { get; init; }

    public required string? ModelId { get; init; }

    public required string? ModelOverrideId { get; init; }

    public required Guid? WorkspaceId { get; init; }

    public required ReadOnlyMemory<byte>? BindingFingerprint { get; init; }

    public required string? Task { get; init; }

    public required string? Instructions { get; init; }

    public required string? Result { get; init; }

    public required string? DisplayMessage { get; init; }

    public required string? FailureCode { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long? ClaimedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long? PayloadExpiresAtUtc { get; init; }

    public required long? CompactedAtUtc { get; init; }

    public required bool PayloadExpired { get; init; }

    public bool IsAgenticAutoApprove { get; init; }

    public string? RequestingKeyPrefix { get; init; }
}

public sealed record McpAgentRunLedgerCounters
{
    public required int AccountingVersion { get; init; }

    public required long NonterminalRunCount { get; init; }

    public required long QueuedRunCount { get; init; }

    public required long RunningRunCount { get; init; }

    public required long IdentityCount { get; init; }

    public required long ActivePayloadBytes { get; init; }

    public required long TombstoneLogicalBytes { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class McpAgentRunLedgerVerification
{
    public required bool IsConsistent { get; init; }

    public required McpAgentRunLedgerCounters Persisted { get; init; }

    public required McpAgentRunLedgerCounters Reconstructed { get; init; }
}

public sealed class McpAgentRunLedgerSnapshot
{
    public required long QueueDepth { get; init; }

    public required long RunningCount { get; init; }

    public required McpAgentRunLedgerCounters Counters { get; init; }
}
