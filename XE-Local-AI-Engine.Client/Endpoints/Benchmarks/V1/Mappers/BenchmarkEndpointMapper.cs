namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

internal static class BenchmarkEndpointMapper
{
    // An omitted rubric takes the default; an incomplete judge still fails validation exactly as it did before.
    public static BenchmarkProjectDraft ToDraft(this BenchmarkProjectMutationRequest request, Guid id) =>
        new(id,
            request.Name,
            request.CoreTask,
            request.ContextTokens,
            request.AgentDefinitionId,
            request.JudgeEnabled
                ? new BenchmarkJudgePolicyDraft(request.JudgeModelName ?? string.Empty,
                    request.JudgeContextTokens ?? 0,
                    request.Rubric.ToRubric(),
                    request.ReferenceAnswer)
                : null,
            request.MaxOutputTokens);

    public static BenchmarkProjectSummaryResponse ToSummary(this BenchmarkProjectRecord project, int runCount) =>
        new()
        {
            Id = project.Id,
            Name = project.Name,
            ContextTokens = project.ContextTokens,
            MaxOutputTokens = project.MaxOutputTokens,
            AgentDefinitionId = project.AgentDefinitionId,
            JudgeEnabled = project.JudgeEnabled,
            RunCount = runCount,
            IsFrozen = project.IsFrozen,
            Version = project.Version,
            CreatedAtUtc = project.CreatedAtUtc,
            UpdatedAtUtc = project.UpdatedAtUtc
        };

    /// <param name="judge">The decrypted current judge policy, or a disabled marker when the project does not judge.</param>
    public static BenchmarkProjectDetailResponse ToDetail(this BenchmarkProjectRecord project,
        int runCount,
        BenchmarkJudgePolicyResponse? judge = null) =>
        new()
        {
            Id = project.Id,
            Name = project.Name,
            CoreTask = JsonSerializer.Deserialize<string>(project.CoreTaskJson.Span)
                       ?? throw new BenchmarkValidationException("The benchmark task is required."),
            ContextTokens = project.ContextTokens,
            MaxOutputTokens = project.MaxOutputTokens,
            AgentDefinitionId = project.AgentDefinitionId,
            JudgeEnabled = project.JudgeEnabled,
            Judge = judge ?? new BenchmarkJudgePolicyResponse
            {
                Enabled = project.JudgeEnabled
            },
            RunCount = runCount,
            IsFrozen = project.IsFrozen,
            Version = project.Version,
            CreatedAtUtc = project.CreatedAtUtc,
            UpdatedAtUtc = project.UpdatedAtUtc
        };

    public static BenchmarkRunSummaryResponse ToSummary(this BenchmarkRunRecord run)
    {
        var summary = new BenchmarkRunSummaryResponse
        {
            Id = run.Id,
            ProjectId = run.ProjectId,
            PrimaryModelName = run.PrimaryModelName,
            PrimaryModelOrigin = run.PrimaryModelOrigin,
            ModelContentFingerprint = run.ModelContentFingerprint,
            AgentName = run.AgentName,
            AgentVersion = run.AgentVersion,
            RequestedContextTokens = run.RequestedContextTokens,
            PrimaryStatus = run.PrimaryStatus,
            Judge = run.ToJudge(),
            QualityScore = run.QualityScore,
            QualityScoreSource = run.QualityScoreSource ?? BenchmarkQualityScoreSources.None,
            Rank = run.Rank,
            RankExclusionReason = run.Judge?.RankExclusionReason,
            PrimaryStopReason = run.PrimaryStopReason,
            ModelGroupKey = BenchmarkModelGroupKey.From(run.PrimaryModelName, run.PrimaryModelOrigin),
            RepeatGroupId = run.RepeatGroupId,
            RepeatIndex = run.RepeatIndex,
            IsWarmup = run.IsWarmup,
            EffectiveContextTokens = run.EffectiveContextTokens,
            DurationMs = run.DurationMs,
            TotalTokens = run.TotalTokens,
            TokensPerSecond = run.TokensPerSecond,
            TtftMs = run.Throughput?.TtftMs,
            PromptTokens = run.Throughput?.PromptTokens,
            PromptTokensPerSecond = run.Throughput?.PromptTokensPerSecond,
            GenerationTokens = run.Throughput?.GenerationTokens,
            GenerationTokensPerSecond = run.Throughput?.GenerationTokensPerSecond,
            CachedPromptTokens = run.Throughput?.CachedPromptTokens,
            SegmentCount = run.Throughput?.SegmentCount,
            UserScore = run.UserScore,
            LastStreamSequence = run.LastStreamSequence,
            Version = run.Version,
            CreatedAtUtc = run.CreatedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc
        };
        ApplyLaunchEvidence(summary, run);
        return summary;
    }

    /// <param name="verdict">The decrypted rubric verdict of the run's current attempt, or null when it has none.</param>
    public static BenchmarkRunDetailResponse ToDetail(this BenchmarkRunRecord run, BenchmarkJudgeResultV2? verdict = null)
    {
        var detail = new BenchmarkRunDetailResponse
        {
            Id = run.Id,
            ProjectId = run.ProjectId,
            PrimaryModelName = run.PrimaryModelName,
            PrimaryModelOrigin = run.PrimaryModelOrigin,
            ModelContentFingerprint = run.ModelContentFingerprint,
            AgentName = run.AgentName,
            AgentVersion = run.AgentVersion,
            RequestedContextTokens = run.RequestedContextTokens,
            PrimaryStatus = run.PrimaryStatus,
            Judge = run.ToJudge(verdict),
            QualityScore = run.QualityScore,
            QualityScoreSource = run.QualityScoreSource ?? BenchmarkQualityScoreSources.None,
            Rank = run.Rank,
            RankExclusionReason = run.Judge?.RankExclusionReason,
            PrimaryStopReason = run.PrimaryStopReason,
            ModelGroupKey = BenchmarkModelGroupKey.From(run.PrimaryModelName, run.PrimaryModelOrigin),
            RepeatGroupId = run.RepeatGroupId,
            RepeatIndex = run.RepeatIndex,
            IsWarmup = run.IsWarmup,
            EffectiveContextTokens = run.EffectiveContextTokens,
            DurationMs = run.DurationMs,
            TotalTokens = run.TotalTokens,
            TokensPerSecond = run.TokensPerSecond,
            TtftMs = run.Throughput?.TtftMs,
            PromptTokens = run.Throughput?.PromptTokens,
            PromptTokensPerSecond = run.Throughput?.PromptTokensPerSecond,
            GenerationTokens = run.Throughput?.GenerationTokens,
            GenerationTokensPerSecond = run.Throughput?.GenerationTokensPerSecond,
            CachedPromptTokens = run.Throughput?.CachedPromptTokens,
            SegmentCount = run.Throughput?.SegmentCount,
            OutputParts = ParseJson(run.OutputPartsJson),
            UserScore = run.UserScore,
            LastStreamSequence = run.LastStreamSequence,
            PrimaryErrorMessage = run.PrimaryErrorMessage,
            Version = run.Version,
            CreatedAtUtc = run.CreatedAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            PrimaryCompletedAtUtc = run.PrimaryCompletedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            PrimaryLaunchReceipt = ParseJson(run.PrimaryLaunchEvidence?.ReceiptJson),
            PrimaryEnvironmentFacts = ParseJson(run.PrimaryLaunchEvidence?.EnvironmentFactsJson)
        };
        ApplyLaunchEvidence(detail, run);
        return detail;
    }

    public static EligibleBenchmarkAgentResponse ToResponse(this BenchmarkEligibleAgent agent) =>
        new()
        {
            Id = agent.Id,
            Name = agent.Name,
            Version = agent.Version
        };

    public static EligibleBenchmarkModelResponse ToResponse(this BenchmarkEligibleModel model) =>
        new()
        {
            ModelName = model.ModelName,
            MaxContextTokens = model.MaxContextTokens,
            EffectiveContextTokens = model.EffectiveContextTokens,
            Origin = model.Origin,
            ModelContentFingerprint = model.ModelContentFingerprint,
            SupportsTools = model.SupportsTools
        };

    /// <summary>
    ///     Fills the flat launch columns both responses carry. Every value is a stored column, so listing a project's
    ///     runs never decrypts or parses a receipt payload.
    /// </summary>
    private static void ApplyLaunchEvidence(BenchmarkRunSummaryResponse response, BenchmarkRunRecord run)
    {
        var primaryIntent = run.PrimaryLaunchIntent;
        var primary = run.PrimaryLaunchEvidence;
        response.PrimaryVariant = primaryIntent?.Variant;
        response.PrimaryKvCacheType = primaryIntent?.KvCacheType;
        response.PrimaryKvCacheTypeSource = primaryIntent?.KvCacheTypeSource;
        response.PrimaryKvAutoReason = primaryIntent?.KvAutoReason;
        response.PrimaryFlashAttentionMode = primaryIntent?.FlashAttentionMode;
        response.PrimaryIntendedLaunchIdentity = primaryIntent?.IntendedLaunchIdentity;
        response.PrimaryIntendedExecutableSha256 = primaryIntent?.IntendedExecutableSha256;
        response.PrimaryEffectiveLaunchIdentity = primary?.EffectiveLaunchIdentity;
        response.PrimaryEffectiveBackend = primary?.EffectiveBackend;
        response.PrimaryPlacementOffloaded = primary?.PlacementOffloaded;
        response.PrimaryPlacementTotal = primary?.PlacementTotal;
        response.PrimaryExecutableSha256 = primary?.ExecutableSha256;
        response.PrimaryHasAuxAssets = primary?.HasAuxAssets;
        response.PrimaryReceiptHash = primary?.ReceiptHash;
        response.PrimaryEnvironmentFactsHash = primary?.EnvironmentFactsHash;
    }

    private static BenchmarkRunJudgeResponse ToJudge(this BenchmarkRunRecord run, BenchmarkJudgeResultV2? verdict = null)
    {
        var judge = run.Judge;
        return new BenchmarkRunJudgeResponse
        {
            State = judge?.State ?? BenchmarkRunJudgeStates.None,
            Score = judge?.Score,
            PolicyRevision = judge?.PolicyRevision,
            AttemptSequence = judge?.AttemptSequence,
            CohortGeneration = judge?.CohortGeneration,
            ExecutionKey = judge?.ExecutionKey,
            PolicyCurrent = judge?.PolicyCurrent ?? false,
            ExecutionCurrent = judge?.ExecutionCurrent ?? false,
            ErrorMessage = judge?.ErrorMessage,
            Summary = verdict?.Summary,
            Criteria = verdict?.Criteria.Select(static criterion => new BenchmarkJudgeCriterionScoreResponse
                              {
                                  Id = criterion.Id,
                                  Score = criterion.Score,
                                  Rationale = criterion.Rationale
                              })
                              .ToArray()
        };
    }

    public static BenchmarkRubricDto ToDto(this BenchmarkJudgeRubricV1 rubric) =>
        new()
        {
            Version = rubric.Version,
            Criteria = rubric.Criteria.Select(static criterion => new BenchmarkRubricCriterionDto
                             {
                                 Id = criterion.Id,
                                 Title = criterion.Title,
                                 Description = criterion.Description,
                                 Weight = criterion.Weight
                             })
                             .ToArray()
        };

    public static BenchmarkJudgeRubricV1? ToRubric(this BenchmarkRubricDto? dto) =>
        dto is null
            ? null
            : new BenchmarkJudgeRubricV1(dto.Version,
                dto.Criteria.Select(static criterion => new BenchmarkJudgeRubricCriterionV1(criterion.Id,
                       criterion.Title,
                       criterion.Description,
                       criterion.Weight))
                   .ToArray());

    /// <summary>
    ///     The project's judge configuration. <paramref name="policy" /> is the decrypted current revision, or null
    ///     when judging is off — which is a state, not an absence, so the object is always present.
    /// </summary>
    public static BenchmarkJudgePolicyResponse ToJudgePolicy(BenchmarkJudgePolicyRevisionRecord? revision, BenchmarkJudgePolicyV1? policy) =>
        revision is null || policy is null
            ? new BenchmarkJudgePolicyResponse
            {
                Enabled = false
            }
            : new BenchmarkJudgePolicyResponse
            {
                Enabled = true,
                PolicyRevisionId = revision.Id,
                PolicyRevision = revision.Revision,
                PolicyHash = revision.PolicyHash,
                ModelName = policy.Model.ModelName,
                RequestedContextTokens = policy.RequestedContextTokens,
                Rubric = policy.Rubric.ToDto(),
                ReferenceAnswer = policy.ReferenceAnswer,
                CohortGeneration = revision.CohortGeneration,
                ReferenceExecutionKey = revision.ReferenceExecutionKey
            };

    private static JsonElement? ParseJson(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } value || value.IsEmpty)
        {
            return null;
        }

        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
