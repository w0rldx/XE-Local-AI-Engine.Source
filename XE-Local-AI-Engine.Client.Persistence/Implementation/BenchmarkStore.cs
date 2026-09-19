namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore : IBenchmarkStore
{
    private const string InterruptedMessage = "Interrupted by application restart.";
    internal const string FidelityKindPerplexity = "ppl";
    internal const string FidelityKindKld = "kld";
    private const string UnresolvedJudgeRuntimeMessage = "judge runtime unresolved";
    internal const string LegacyTaskHash = "v1:legacy";
    private const string VerdictA = "a";
    private const string VerdictB = "b";
    private const string VerdictTie = "tie";

    /// <summary>Both presentation orders of every pair, always: the swap is what cancels the judge's position bias.</summary>
    private static readonly int[] ComparisonOrders = [0, 1];

    /// <summary>Web defaults, matching the canonical writer the fit's scores were serialized with.</summary>
    private static readonly JsonSerializerOptions PairwiseScoreOptions = new(JsonSerializerDefaults.Web);

    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public BenchmarkStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    private static void EnsurePolicyHash([NotNull] string? policyHash)
    {
        if (policyHash is not { Length: 64 } || !policyHash.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new BenchmarkValidationException("A judge policy hash must be 64 lowercase hexadecimal characters.");
        }
    }

    private static void TerminalizeWork(BenchmarkWorkItem work,
        BenchmarkWorkStatus status,
        string? errorMessage,
        long now)
    {
        if (work.Status is BenchmarkWorkStatus.Succeeded or BenchmarkWorkStatus.Failed or BenchmarkWorkStatus.Cancelled)
        {
            return;
        }

        work.Status = status;
        work.ErrorMessage = errorMessage;
        work.FinishedAtUtc = now;
        work.Version++;
    }

    /// <summary>
    ///     The run's work item of that kind. A run has exactly one primary item, but one judge item per attempt, so the
    ///     newest queue sequence is taken — that is the current attempt's, which is the only one still in play.
    /// </summary>
    private async Task<BenchmarkWorkItem> RequireWorkAsync(Guid runId, BenchmarkWorkKind kind, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkWorkItems.Where(entity => entity.RunId == runId && entity.Kind == kind)
                        .OrderByDescending(entity => entity.QueueSequence)
                        .FirstOrDefaultAsync(cancellationToken)
        ?? throw new BenchmarkNotFoundException("Benchmark work item was not found.");

    private async Task AcquireWorkCompletionAsync(Guid runId,
        BenchmarkWorkKind kind,
        long expectedWorkVersion,
        CancellationToken cancellationToken)
    {
        // Reserve SQLite's single writer before reading the aggregate. Score and cancellation updates then serialize
        // around phase completion without participating in the executor's work-item compare-and-swap token.
        var acquired = await _dbContext.BenchmarkWorkItems
                                       .Where(entity => entity.RunId == runId
                                                        && entity.Kind == kind
                                                        && entity.Status == BenchmarkWorkStatus.Running
                                                        && entity.Version == expectedWorkVersion)
                                       .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.Version, entity => entity.Version), cancellationToken);
        if (acquired == 0)
        {
            throw new BenchmarkConflictException("VersionConflict");
        }
    }

    private async Task<BenchmarkProject> RequireProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkProjects.SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
        ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");

    private async Task<BenchmarkRun> RequireRunAsync(Guid runId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _dbContext.BenchmarkRuns.AsQueryable() : _dbContext.BenchmarkRuns.AsNoTracking();
        return await query.SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken)
               ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new BenchmarkConflictException("VersionConflict", exception)
            {
                Source = exception.Source
            };
        }
        catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new BenchmarkConflictException("DuplicateWork", exception)
            {
                Source = exception.Source
            };
        }
    }

    private static void ValidateProject(BenchmarkProjectInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name) || input.CoreTaskJson.Length == 0 || input.ContextTokens <= 0 || input.AgentDefinitionId == Guid.Empty)
        {
            throw new BenchmarkValidationException("Benchmark project input is invalid.");
        }
    }

    private static void ValidateStart(BenchmarkStartRunCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ProjectId == Guid.Empty || command.RuntimeSnapshotJson.Length == 0 || string.IsNullOrWhiteSpace(command.PrimaryModelName)
            || string.IsNullOrWhiteSpace(command.ModelContentFingerprint) || !command.ModelContentFingerprint.StartsWith("v1:", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(command.AgentName) || command.RequestedContextTokens <= 0)
        {
            throw new BenchmarkValidationException("Benchmark run input is invalid.");
        }
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
        {
            throw new BenchmarkConflictException("VersionConflict");
        }
    }

    private static void EnsurePrimaryState(BenchmarkRun run, BenchmarkPrimaryStatus required)
    {
        if (run.PrimaryStatus != required)
        {
            throw new BenchmarkConflictException("InvalidPrimaryTransition");
        }
    }

    private static bool IsPrimaryTerminal(BenchmarkPrimaryStatus status) =>
        status is BenchmarkPrimaryStatus.Succeeded or BenchmarkPrimaryStatus.Failed or BenchmarkPrimaryStatus.Cancelled;

    private static string Sanitize(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 1024 ? normalized : normalized[..1024];
    }

    private static void UpdateLastStreamSequence(BenchmarkRun run, long sequence)
    {
        if (sequence > run.LastStreamSequence)
        {
            run.LastStreamSequence = sequence;
        }
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static BenchmarkProjectRecord ToRecord(BenchmarkProject entity, bool frozen) =>
        new()
        {
            Id = entity.Id,
            Name = entity.Name,
            CoreTaskJson = entity.CoreTaskJson.ToArray(),
            ContextTokens = entity.ContextTokens,
            AgentDefinitionId = entity.AgentDefinitionId,
            JudgeEnabled = entity.CurrentJudgePolicyRevisionId is not null,
            CurrentJudgePolicyRevisionId = entity.CurrentJudgePolicyRevisionId,
            IsFrozen = frozen,
            Version = entity.Version,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            MaxOutputTokens = entity.MaxOutputTokens,
            InvocationTimeoutSeconds = entity.InvocationTimeoutSeconds,
            ReasoningBudgetTokens = entity.ReasoningBudgetTokens,
            FidelityEnabled = entity.FidelityEnabled,
            FidelityKldEnabled = entity.FidelityKldEnabled,
            FidelityChunks = entity.FidelityChunks,
            FidelityKldBaseModelName = entity.FidelityKldBaseModelName,
            FidelityKldBaseFingerprint = entity.FidelityKldBaseFingerprint,
            TaskItemSetHash = entity.TaskItemSetHash
        };

    // One place writes the seven throughput columns, so the success path and the cancel-reset path can never disagree
    // about which of them a run carries.
    private static void ApplyThroughput(BenchmarkRun run, BenchmarkRunThroughput? throughput)
    {
        run.TtftMs = throughput?.TtftMs;
        run.PromptTokens = throughput?.PromptTokens;
        run.PromptMs = throughput?.PromptMs;
        run.GenerationTokens = throughput?.GenerationTokens;
        run.GenerationMs = throughput?.GenerationMs;
        run.CachedPromptTokens = throughput?.CachedPromptTokens;
        run.SegmentCount = throughput?.SegmentCount;
    }

    private static BenchmarkRunThroughput? ToThroughput(BenchmarkRun entity) =>
        entity.TtftMs is null
        && entity.PromptTokens is null
        && entity.PromptMs is null
        && entity.GenerationTokens is null
        && entity.GenerationMs is null
        && entity.CachedPromptTokens is null
        && entity.SegmentCount is null
            ? null
            : new BenchmarkRunThroughput
            {
                TtftMs = entity.TtftMs,
                PromptTokens = entity.PromptTokens,
                PromptMs = entity.PromptMs,
                GenerationTokens = entity.GenerationTokens,
                GenerationMs = entity.GenerationMs,
                CachedPromptTokens = entity.CachedPromptTokens,
                SegmentCount = entity.SegmentCount
            };

    private static BenchmarkRunRecord ToRecord(BenchmarkRun entity) =>
        new()
        {
            Id = entity.Id,
            ProjectId = entity.ProjectId,
            RuntimeSnapshotJson = entity.RuntimeSnapshotJson.ToArray(),
            PrimaryModelName = entity.PrimaryModelName,
            PrimaryModelOrigin = entity.PrimaryModelOrigin,
            ModelContentFingerprint = entity.ModelContentFingerprint,
            AgentName = entity.AgentName,
            AgentVersion = entity.AgentVersion,
            RequestedContextTokens = entity.RequestedContextTokens,
            PrimaryStatus = entity.PrimaryStatus,
            EffectiveContextTokens = entity.EffectiveContextTokens,
            DurationMs = entity.DurationMs,
            TotalTokens = entity.TotalTokens,
            TokensPerSecond = entity.TokensPerSecond,
            OutputPartsJson = CopyOptional(entity.OutputPartsJson),
            LastStreamSequence = entity.LastStreamSequence,
            UserScore = entity.UserScore,
            PrimaryErrorMessage = entity.PrimaryErrorMessage,
            Version = entity.Version,
            CreatedAtUtc = entity.CreatedAtUtc,
            StartedAtUtc = entity.StartedAtUtc,
            PrimaryCompletedAtUtc = entity.PrimaryCompletedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            PrimaryLaunchIntent = ToIntent(entity.PrimaryVariant, entity.PrimaryKvCacheType, entity.PrimaryKvCacheTypeSource, entity.PrimaryKvAutoReason,
                entity.PrimaryFlashAttentionMode, entity.PrimaryIntendedLaunchIdentity, entity.PrimaryIntendedExecutableSha256,
                entity.PrimaryLaunchIdentityScheme),
            PrimaryLaunchEvidence = ToEvidence(entity.PrimaryLaunchReceiptJson, entity.PrimaryEnvironmentFactsJson, entity.PrimaryReceiptHash,
                entity.PrimaryEnvironmentFactsHash, entity.PrimaryEffectiveLaunchIdentity, entity.PrimaryEffectiveBackend,
                entity.PrimaryPlacementOffloaded, entity.PrimaryPlacementTotal, entity.PrimaryLaunchExecutableSha256,
                entity.PrimaryLaunchHasAuxAssets, entity.PrimaryLaunchKvCacheTypeSource),
            PrimaryStopReason = entity.PrimaryStopReason,
            Throughput = ToThroughput(entity),
            RepeatGroupId = entity.RepeatGroupId,
            RepeatIndex = entity.RepeatIndex,
            IsWarmup = entity.IsWarmup,
            InvocationTimeoutSeconds = entity.InvocationTimeoutSeconds,
            RepeatMode = entity.RepeatMode,
            SamplingSeed = entity.SamplingSeed,
            SamplingTemperature = entity.SamplingTemperature,
            Fidelity = ToFidelity(entity),
            TaskItemId = entity.TaskItemId,
            TaskItemIndex = entity.TaskItemIndex,
            CellKey = entity.CellKey,
            TaskInputHash = entity.TaskInputHash,
            TaskItemSetHash = entity.TaskItemSetHash
        };

    /// <summary>
    ///     Null when nothing has ever been measured, so the API says "no measurement" rather than a projection of
    ///     thirteen nulls that a reader has to interpret.
    /// </summary>
    private static BenchmarkRunFidelity? ToFidelity(BenchmarkRun entity) =>
        entity.FidelityStatus is null
            ? null
            : new BenchmarkRunFidelity
            {
                Status = entity.FidelityStatus,
                AttemptId = entity.FidelityAttemptId,
                PerplexityMean = entity.PerplexityMean,
                PerplexityStdErr = entity.PerplexityStdErr,
                PerplexityChunks = entity.PerplexityChunks,
                PerplexityContextTokens = entity.PerplexityContextTokens,
                PerplexityCorpusId = entity.PerplexityCorpusId,
                KldMean = entity.KldMean,
                KldP99 = entity.KldP99,
                TopTokenAgreement = entity.TopTokenAgreement,
                KldBaseFingerprint = entity.KldBaseFingerprint,
                KldBaseLogitsDigest = entity.KldBaseLogitsDigest,
                ErrorMessage = entity.FidelityErrorMessage
            };

    private static BenchmarkFidelityAttemptRecord ToRecord(BenchmarkFidelityAttempt entity) =>
        new()
        {
            Id = entity.Id,
            RunId = entity.RunId,
            Sequence = entity.Sequence,
            Kind = entity.Kind,
            Status = entity.Status,
            PerplexityMean = entity.PerplexityMean,
            PerplexityStdErr = entity.PerplexityStdErr,
            PerplexityChunks = entity.PerplexityChunks,
            PerplexityContextTokens = entity.PerplexityContextTokens,
            CorpusId = entity.CorpusId,
            KldMean = entity.KldMean,
            KldP99 = entity.KldP99,
            TopTokenAgreement = entity.TopTokenAgreement,
            BaseModelName = entity.BaseModelName,
            BaseModelContentFingerprint = entity.BaseModelContentFingerprint,
            BaseLogitsDigest = entity.BaseLogitsDigest,
            ErrorMessage = entity.ErrorMessage,
            EnqueuedAtUtc = entity.EnqueuedAtUtc,
            StartedAtUtc = entity.StartedAtUtc,
            CompletedAtUtc = entity.CompletedAtUtc
        };

    private static BenchmarkRunLaunchIntent? ToIntent(string? variant,
        string? kvCacheType,
        string? kvCacheTypeSource,
        string? kvAutoReason,
        string? flashAttentionMode,
        string? intendedLaunchIdentity,
        string? intendedExecutableSha256,
        int? launchIdentityScheme) =>
        variant is null || kvCacheType is null || kvCacheTypeSource is null || flashAttentionMode is null || intendedLaunchIdentity is null
            ? null
            : new BenchmarkRunLaunchIntent
            {
                Variant = variant,
                KvCacheType = kvCacheType,
                KvCacheTypeSource = kvCacheTypeSource,
                KvAutoReason = kvAutoReason,
                FlashAttentionMode = flashAttentionMode,
                IntendedLaunchIdentity = intendedLaunchIdentity,
                IntendedExecutableSha256 = intendedExecutableSha256,
                LaunchIdentityScheme = launchIdentityScheme
            };

    private static BenchmarkRunLaunchEvidence? ToEvidence(byte[]? receiptJson,
        byte[]? environmentFactsJson,
        string? receiptHash,
        string? environmentFactsHash,
        string? effectiveLaunchIdentity,
        string? effectiveBackend,
        int? placementOffloaded,
        int? placementTotal,
        string? executableSha256,
        bool? hasAuxAssets,
        string? kvCacheTypeSource) =>
        receiptJson is null && environmentFactsJson is null
            ? null
            : new BenchmarkRunLaunchEvidence
            {
                ReceiptJson = CopyOptional(receiptJson),
                EnvironmentFactsJson = CopyOptional(environmentFactsJson),
                ReceiptHash = receiptHash,
                EnvironmentFactsHash = environmentFactsHash,
                EffectiveLaunchIdentity = effectiveLaunchIdentity,
                EffectiveBackend = effectiveBackend,
                PlacementOffloaded = placementOffloaded,
                PlacementTotal = placementTotal,
                ExecutableSha256 = executableSha256,
                HasAuxAssets = hasAuxAssets,
                KvCacheTypeSource = kvCacheTypeSource
            };

    private static BenchmarkJudgePolicyRevisionRecord ToRecord(BenchmarkJudgePolicyRevision entity, bool includePayload) =>
        new()
        {
            Id = entity.Id,
            ProjectId = entity.ProjectId,
            Revision = entity.Revision,
            PolicyJson = includePayload ? CopyOptional(entity.PolicyJson) : null,
            PolicyHash = entity.PolicyHash,
            ReferenceExecutionKey = entity.ReferenceExecutionKey,
            CohortGeneration = entity.CohortGeneration,
            CreatedAtUtc = entity.CreatedAtUtc,
            ComparisonSetVersion = entity.ComparisonSetVersion
        };

    private static BenchmarkJudgeAttemptRecord ToRecord(BenchmarkJudgeAttempt entity) =>
        new()
        {
            Id = entity.Id,
            RunId = entity.RunId,
            Sequence = entity.Sequence,
            PolicyRevisionId = entity.PolicyRevisionId,
            CohortGeneration = entity.CohortGeneration,
            JudgeRuntimeJson = CopyOptional(entity.JudgeRuntimeJson),
            JudgeExecutionKey = entity.JudgeExecutionKey,
            Status = entity.Status,
            ResultJson = CopyOptional(entity.ResultJson),
            Score = entity.Score,
            ErrorMessage = entity.ErrorMessage,
            EnqueuedAtUtc = entity.EnqueuedAtUtc,
            StartedAtUtc = entity.StartedAtUtc,
            CompletedAtUtc = entity.CompletedAtUtc,
            Version = entity.Version,
            LaunchIntent = ToIntent(entity.Variant, entity.KvCacheType, entity.KvCacheTypeSource, entity.KvAutoReason, entity.FlashAttentionMode,
                entity.IntendedLaunchIdentity, entity.IntendedExecutableSha256, entity.LaunchIdentityScheme),
            LaunchEvidence = ToEvidence(entity.LaunchReceiptJson, entity.EnvironmentFactsJson, entity.ReceiptHash, entity.EnvironmentFactsHash,
                entity.EffectiveLaunchIdentity, entity.EffectiveBackend, entity.PlacementOffloaded, entity.PlacementTotal,
                entity.LaunchExecutableSha256, entity.LaunchHasAuxAssets, entity.LaunchKvCacheTypeSource)
        };

    private static ReadOnlyMemory<byte>? CopyOptional(byte[]? value)
    {
        if (value is null)
        {
            return default;
        }

        return new ReadOnlyMemory<byte>(value.ToArray());
    }
}
