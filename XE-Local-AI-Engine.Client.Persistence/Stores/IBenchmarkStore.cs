namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public interface IBenchmarkStore
{
    Task<BenchmarkProjectRecord> CreateProjectAsync(BenchmarkProjectInput input, CancellationToken cancellationToken = default);
    Task<BenchmarkProjectRecord?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task<BenchmarkProjectRecord> UpdateProjectAsync(Guid projectId, long expectedVersion, BenchmarkProjectInput input, CancellationToken cancellationToken = default);
    Task DeleteProjectAsync(Guid projectId, long expectedVersion, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> StartRunAsync(BenchmarkStartRunCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkRunRecord>> ListRunsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<BenchmarkClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkPrimarySucceededAsync(BenchmarkPrimarySuccessCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) => MarkPrimaryFailedAsync(runId, expectedRunVersion, errorMessage, cancellationToken);
    Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) => MarkPrimaryCancelledAsync(runId, expectedRunVersion, cancellationToken);
    Task<BenchmarkRunRecord> MarkJudgeSucceededAsync(BenchmarkJudgeSuccessCommand command, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) => MarkJudgeFailedAsync(runId, expectedRunVersion, errorMessage, cancellationToken);
    Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) => MarkJudgeCancelledAsync(runId, expectedRunVersion, cancellationToken);
    Task<BenchmarkRunRecord> CancelAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);
    Task<BenchmarkRunRecord> SetUserScoreAsync(Guid runId, int score, long expectedRunVersion, CancellationToken cancellationToken = default);
    Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<BenchmarkRunRecord>> RecoverRunsOnStartupAsync(CancellationToken cancellationToken = default)
    {
        _ = await RecoverOnStartupAsync(cancellationToken).ConfigureAwait(false);
        return [];
    }
    Task DeleteRunAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default);
}

public sealed record BenchmarkProjectInput(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    bool JudgeEnabled,
    string? JudgeModelName,
    int? JudgeContextTokens,
    int JudgePromptVersion = 1,
    int JudgeOutputSchemaVersion = 1);

public sealed record BenchmarkStartRunCommand(
    Guid RunId,
    Guid ProjectId,
    long ExpectedProjectVersion,
    ReadOnlyMemory<byte> RuntimeSnapshotJson,
    string PrimaryModelName,
    LocalModelOrigin? PrimaryModelOrigin,
    string ModelContentFingerprint,
    string AgentName,
    long AgentVersion,
    int RequestedContextTokens,
    bool JudgeEnabled,
    IBenchmarkFreezeCommitGuard? FreezeCommitGuard = null);

/// <summary>
///     Application-owned dependency guard executed by <see cref="IBenchmarkStore.StartRunAsync" /> inside the same
///     transaction that verifies the project version and inserts the run/work rows. Returning <see langword="false" />
///     aborts the transaction with <c>FreezeDependencyChanged</c>.
/// </summary>
public interface IBenchmarkFreezeCommitGuard
{
    Task<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

public sealed record BenchmarkPrimarySuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    ReadOnlyMemory<byte> OutputPartsJson,
    long LastStreamSequence,
    int EffectiveContextTokens,
    long DurationMs,
    int? TotalTokens,
    double? TokensPerSecond);

public sealed record BenchmarkJudgeSuccessCommand(
    Guid RunId,
    long ExpectedWorkVersion,
    ReadOnlyMemory<byte> JudgeResultJson,
    long LastStreamSequence = 0);

public sealed record BenchmarkProjectRecord(
    Guid Id,
    string Name,
    ReadOnlyMemory<byte> CoreTaskJson,
    int ContextTokens,
    Guid AgentDefinitionId,
    bool JudgeEnabled,
    string? JudgeModelName,
    int? JudgeContextTokens,
    int JudgePromptVersion,
    int JudgeOutputSchemaVersion,
    bool IsFrozen,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public sealed record BenchmarkRunRecord(
    Guid Id,
    Guid ProjectId,
    ReadOnlyMemory<byte> RuntimeSnapshotJson,
    string PrimaryModelName,
    LocalModelOrigin? PrimaryModelOrigin,
    string ModelContentFingerprint,
    string AgentName,
    long AgentVersion,
    int RequestedContextTokens,
    BenchmarkPrimaryStatus PrimaryStatus,
    int? EffectiveContextTokens,
    long? DurationMs,
    int? TotalTokens,
    double? TokensPerSecond,
    ReadOnlyMemory<byte>? OutputPartsJson,
    long LastStreamSequence,
    int? UserScore,
    BenchmarkJudgeStatus JudgeStatus,
    ReadOnlyMemory<byte>? JudgeResultJson,
    string? PrimaryErrorMessage,
    string? JudgeErrorMessage,
    long Version,
    long CreatedAtUtc,
    long? StartedAtUtc,
    long? PrimaryCompletedAtUtc,
    long? JudgeStartedAtUtc,
    long? JudgeCompletedAtUtc,
    long UpdatedAtUtc);

public sealed record BenchmarkClaimedWork(
    long QueueSequence,
    Guid RunId,
    BenchmarkWorkKind Kind,
    int Attempt,
    long Version,
    BenchmarkRunRecord Run);

public abstract class BenchmarkStoreException(string message) : InvalidOperationException(message);
public sealed class BenchmarkNotFoundException(string message) : BenchmarkStoreException(message);
public sealed class BenchmarkConflictException(string code) : BenchmarkStoreException(code)
{
    public string Code { get; } = code;
}
public sealed class BenchmarkValidationException(string message) : BenchmarkStoreException(message);
