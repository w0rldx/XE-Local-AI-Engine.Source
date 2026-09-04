namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

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
            request.MaxOutputTokens,
            request.InvocationTimeoutSeconds,
            request.ReasoningBudgetTokens,
            request.FidelityEnabled,
            request.FidelityKldEnabled,
            request.FidelityChunks,
            request.FidelityKldBaseModelName);

    public static BenchmarkTaskItemDraft ToDraft(this BenchmarkTaskItemMutationRequest request) =>
        new(request.Prompt,
            request.Kind,
            request.ReferenceAnswer,
            request.VerifierConfig,
            request.GeneratorConfig,
            request.CountsTowardScore);

    public static BenchmarkTaskItemResponse ToResponse(this BenchmarkTaskItemRecord item) =>
        new()
        {
            Id = item.Id,
            ProjectId = item.ProjectId,
            ParentItemId = item.ParentItemId,
            Index = item.Index,
            Kind = item.Kind,
            Revision = item.Revision,
            InputHash = item.InputHash,
            IsLeaf = item.IsLeaf,
            CountsTowardScore = item.CountsTowardScore,
            Prompt = BenchmarkTaskItemService.DecodePrompt(item.PromptJson.Span),
            ReferenceAnswer = BenchmarkTaskItemService.DecodeOptional(item.ReferenceAnswerJson),
            VerifierConfig = ParseJson(item.VerifierConfigJson),
            GeneratorConfig = ParseJson(item.GeneratorConfigJson),
            Version = item.Version,
            CreatedAtUtc = item.CreatedAtUtc,
            UpdatedAtUtc = item.UpdatedAtUtc
        };

    public static ListBenchmarkTaskItemsResponse ToResponse(this IReadOnlyList<BenchmarkTaskItemRecord> items, BenchmarkProjectRecord project) =>
        new()
        {
            Items = [.. items.Select(ToResponse)],
            TaskItemSetHash = project.TaskItemSetHash,
            ProjectVersion = project.Version
        };

    public static BenchmarkProjectSummaryResponse ToSummary(this BenchmarkProjectRecord project, int runCount) =>
        new()
        {
            Id = project.Id,
            Name = project.Name,
            ContextTokens = project.ContextTokens,
            MaxOutputTokens = project.MaxOutputTokens,
            ReasoningBudgetTokens = project.ReasoningBudgetTokens,
            InvocationTimeoutSeconds = project.InvocationTimeoutSeconds,
            AgentDefinitionId = project.AgentDefinitionId,
            JudgeEnabled = project.JudgeEnabled,
            RunCount = runCount,
            IsFrozen = project.IsFrozen,
            Version = project.Version,
            CreatedAtUtc = project.CreatedAtUtc,
            UpdatedAtUtc = project.UpdatedAtUtc
        };

    /// <param name="judge">The decrypted current judge policy, or a disabled marker when the project does not judge.</param>
    /// <param name="taskItems">
    ///     The project's items. Omitted leaves the detail's item list empty rather than guessing: a caller that did
    ///     not read them must not be able to render "this project asks nothing".
    /// </param>
    public static BenchmarkProjectDetailResponse ToDetail(this BenchmarkProjectRecord project,
        int runCount,
        BenchmarkJudgePolicyResponse? judge = null,
        IReadOnlyList<BenchmarkTaskItemRecord>? taskItems = null) =>
        new()
        {
            TaskItems = taskItems is null ? [] : [.. taskItems.Select(ToResponse)],
            TaskItemSetHash = project.TaskItemSetHash,
            Id = project.Id,
            Name = project.Name,
            CoreTask = JsonSerializer.Deserialize<string>(project.CoreTaskJson.Span)
                       ?? throw new BenchmarkValidationException("The benchmark task is required."),
            ContextTokens = project.ContextTokens,
            MaxOutputTokens = project.MaxOutputTokens,
            ReasoningBudgetTokens = project.ReasoningBudgetTokens,
            InvocationTimeoutSeconds = project.InvocationTimeoutSeconds,
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
            UpdatedAtUtc = project.UpdatedAtUtc,
            FidelityEnabled = project.FidelityEnabled,
            FidelityKldEnabled = project.FidelityKldEnabled,
            FidelityChunks = project.FidelityChunks,
            FidelityChunksEffective = BenchmarkFidelityPolicy.ClampChunks(project.FidelityChunks),
            FidelityKldBaseModelName = project.FidelityKldBaseModelName,
            FidelityKldBaseFingerprint = project.FidelityKldBaseFingerprint,
            // The ONE digest expression, through the same helper every run's display gate reads — a second copy is
            // the bug the architecture test exists to catch.
            FidelityKldExpectedDigest = BenchmarkEndpointSupport.ExpectedKldDigest(project)
        };

    /// <param name="expectedKldBaseLogitsDigest">
    ///     The digest the project's CURRENT KL-divergence settings recompute, or null when the project does not
    ///     measure it. A stored KLD figure is served only while the two match; see <see cref="ToFidelity" />.
    /// </param>
    public static BenchmarkRunSummaryResponse ToSummary(this BenchmarkRunRecord run, string? expectedKldBaseLogitsDigest = null)
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
            Fidelity = run.ToFidelity(expectedKldBaseLogitsDigest),
            QualityScore = run.QualityScore,
            QualityScoreSource = run.QualityScoreSource ?? BenchmarkQualityScoreSources.None,
            Rank = run.Rank,
            CellQuality = run.CellQuality,
            RankExclusionReason = run.Judge?.RankExclusionReason,
            TaskItemId = run.TaskItemId,
            TaskItemIndex = run.TaskItemIndex,
            CellKey = run.CellKey,
            TaskInputHash = run.TaskInputHash,
            TaskItemSetHash = run.TaskItemSetHash,
            PrimaryStopReason = run.PrimaryStopReason,
            ModelGroupKey = BenchmarkModelGroupKey.From(run.PrimaryModelName, run.PrimaryModelOrigin),
            RepeatGroupId = run.RepeatGroupId,
            RepeatIndex = run.RepeatIndex,
            IsWarmup = run.IsWarmup,
            RepeatMode = run.RepeatMode,
            SamplingSeed = run.SamplingSeed,
            SamplingTemperature = run.SamplingTemperature,
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

    /// <summary>
    ///     Projects a run's fidelity numbers, WITHHOLDING the KL-divergence trio unless the digest they were measured
    ///     under is the one the project's current settings recompute. The gate is the whole cache key rather than the
    ///     base model's fingerprint: the corpus, the chunk count and the format version all move without the
    ///     fingerprint moving, and p99 in particular is strongly chunk-count dependent, so a fingerprint-only check
    ///     would serve a figure measured over 50 chunks beside one measured over 200 as if they compared.
    /// </summary>
    public static BenchmarkFidelityResponse? ToFidelity(this BenchmarkRunRecord run, string? expectedKldBaseLogitsDigest)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Fidelity is not { } fidelity)
        {
            return null;
        }

        var comparable = BenchmarkKldCacheKey.IsComparable(fidelity.KldBaseLogitsDigest, expectedKldBaseLogitsDigest);
        string kldState;
        if (fidelity.KldMean is null)
        {
            kldState = BenchmarkFidelityKldStates.None;
        }
        else
        {
            kldState = comparable ? BenchmarkFidelityKldStates.Ok : BenchmarkFidelityKldStates.Stale;
        }

        return new BenchmarkFidelityResponse
        {
            Status = fidelity.Status ?? "queued",
            AttemptId = fidelity.AttemptId,
            PerplexityMean = fidelity.PerplexityMean,
            PerplexityStdErr = fidelity.PerplexityStdErr,
            PerplexityChunks = fidelity.PerplexityChunks,
            PerplexityContextTokens = fidelity.PerplexityContextTokens,
            PerplexityCorpusId = fidelity.PerplexityCorpusId,
            KldState = kldState,
            KldMean = comparable ? fidelity.KldMean : null,
            KldP99 = comparable ? fidelity.KldP99 : null,
            TopTokenAgreement = comparable ? fidelity.TopTokenAgreement : null,
            KldBaseFingerprint = fidelity.KldBaseFingerprint,
            ErrorMessage = fidelity.ErrorMessage
        };
    }

    /// <param name="verdict">The decrypted rubric verdict of the run's current attempt, or null when it has none.</param>
    public static BenchmarkRunDetailResponse ToDetail(this BenchmarkRunRecord run,
        BenchmarkJudgeResultV2? verdict = null,
        string? expectedKldBaseLogitsDigest = null)
    {
        var (reasoningBudgetTokens, reasoningBudgetApplicable) = ReadReasoningBudget(run.RuntimeSnapshotJson);
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
            Fidelity = run.ToFidelity(expectedKldBaseLogitsDigest),
            QualityScore = run.QualityScore,
            QualityScoreSource = run.QualityScoreSource ?? BenchmarkQualityScoreSources.None,
            Rank = run.Rank,
            CellQuality = run.CellQuality,
            RankExclusionReason = run.Judge?.RankExclusionReason,
            TaskItemId = run.TaskItemId,
            TaskItemIndex = run.TaskItemIndex,
            CellKey = run.CellKey,
            TaskInputHash = run.TaskInputHash,
            TaskItemSetHash = run.TaskItemSetHash,
            PrimaryStopReason = run.PrimaryStopReason,
            ModelGroupKey = BenchmarkModelGroupKey.From(run.PrimaryModelName, run.PrimaryModelOrigin),
            RepeatGroupId = run.RepeatGroupId,
            RepeatIndex = run.RepeatIndex,
            IsWarmup = run.IsWarmup,
            RepeatMode = run.RepeatMode,
            SamplingSeed = run.SamplingSeed,
            SamplingTemperature = run.SamplingTemperature,
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
            PrimaryEnvironmentFacts = ParseJson(run.PrimaryLaunchEvidence?.EnvironmentFactsJson),
            ReasoningBudgetTokens = reasoningBudgetTokens,
            ReasoningBudgetApplicable = reasoningBudgetApplicable
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
        // Server-computed so the scheme number never reaches the client. A NULL stored scheme is a pre-scheme freeze,
        // i.e. scheme 1, and the guard treats it the same way.
        response.PrimaryLaunchIdentitySchemeOutdated = primaryIntent is null
            ? null
            : (primaryIntent.LaunchIdentityScheme ?? 1) != LlamaServerLaunchProjection.IdentitySchemeVersion;
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
                              .ToArray(),
            Verifiers = verdict?.Verifiers?.Select(static verifier => new BenchmarkJudgeVerifierResponse
                               {
                                   Id = verifier.Id,
                                   Kind = verifier.Kind,
                                   Passed = verifier.Passed,
                                   Detail = verifier.Detail
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
                                 Weight = criterion.Weight,
                                 Kind = BenchmarkJudgeCriterionKinds.Normalize(criterion.Kind),
                                 Config = criterion.Config
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
                       criterion.Weight,
                       BenchmarkJudgeCriterionKinds.Normalize(criterion.Kind),
                       criterion.Config))
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
                Mode = BenchmarkJudgePolicyModes.Normalize(policy.Mode),
                CohortGeneration = revision.CohortGeneration,
                ReferenceExecutionKey = revision.ReferenceExecutionKey,
                PromptVersion = policy.PromptVersion,
                PromptVersionOutdated = policy.PromptVersion != BenchmarkJudgePolicyVersions.PromptVersion
            };

    /// <summary>
    ///     The two frozen reasoning-budget facts, read straight out of the run's snapshot. Two scalars rather than the
    ///     snapshot itself: the payload stays unexposed, and this needs no column of its own. A plain parse, NOT the
    ///     factory's verifying deserialize — this is a display value, and a run whose snapshot no longer validates
    ///     should still render rather than fail its own detail read. Detail only: the listing projection never loads
    ///     the snapshot column, so it arrives empty and both facts are null.
    /// </summary>
    private static (int? Tokens, bool? Applicable) ReadReasoningBudget(ReadOnlyMemory<byte> snapshotJson)
    {
        if (snapshotJson.IsEmpty)
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (!document.RootElement.TryGetProperty("primarySampling", out var sampling)
                || !sampling.TryGetProperty("reasoningBudgetTokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Number)
            {
                return (null, null);
            }

            // Absent means a run frozen before the field existed; the executor reads that as the inert true, so this
            // must agree rather than reporting "not applicable" for every legacy run.
            var applicable = !sampling.TryGetProperty("reasoningBudgetEnforceable", out var enforceable)
                             || enforceable.ValueKind != JsonValueKind.False;
            return (tokens.GetInt32(), applicable);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

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

internal static class BenchmarkCellMapper
{
    public static ListBenchmarkCellsResponse ToResponse(this BenchmarkCellPage page) =>
        new()
        {
            Cells =
            [
                .. page.Cells.Select(static cell => new BenchmarkCellResponse
                {
                    CellKey = cell.CellKey,
                    PrimaryModelName = cell.PrimaryModelName,
                    ModelContentFingerprint = cell.ModelContentFingerprint,
                    KvCacheType = cell.KvCacheType,
                    RepeatGroupId = cell.RepeatGroupId,
                    RepeatIndex = cell.RepeatIndex,
                    Quality = cell.Quality,
                    Rank = cell.Rank,
                    RankExclusionReason = cell.RankExclusionReason,
                    Items =
                    [
                        .. cell.Items.Select(static item => new BenchmarkCellItemResponse
                        {
                            RunId = item.RunId,
                            TaskItemId = item.TaskItemId,
                            TaskItemIndex = item.TaskItemIndex,
                            QualityScore = item.QualityScore,
                            PrimaryStopReason = item.PrimaryStopReason,
                            RankExclusionReason = item.RankExclusionReason
                        })
                    ]
                })
            ],
            RankCohort = new BenchmarkRankCohortResponse
            {
                PolicyRevision = page.RankCohort?.PolicyRevision,
                ExecutionKey = page.RankCohort?.ExecutionKey,
                CohortGeneration = page.RankCohort?.CohortGeneration,
                RankedCount = page.RankCohort?.RankedCount ?? 0,
                TotalScored = page.RankCohort?.TotalScored ?? 0
            },
            ScorableItemCount = page.ScorableItemCount
        };
}
