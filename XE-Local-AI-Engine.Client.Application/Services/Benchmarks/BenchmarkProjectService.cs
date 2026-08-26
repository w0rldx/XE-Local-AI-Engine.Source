namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

public interface IBenchmarkProjectService
{
    Task<BenchmarkProjectRecord> CreateAsync(BenchmarkProjectDraft draft, CancellationToken cancellationToken = default);
    Task<BenchmarkProjectRecord> UpdateAsync(Guid projectId, long expectedVersion, BenchmarkProjectDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Changes (or, with a <see langword="null" /> draft, disables) the judge on a project that may already be
    ///     frozen. The same hash is a no-op; a different hash on a project that already has runs requires
    ///     <paramref name="confirmRejudge" /> and then re-judges every succeeded one.
    /// </summary>
    Task<BenchmarkJudgePolicyChange> UpdateJudgePolicyAsync(Guid projectId,
        long expectedVersion,
        BenchmarkJudgePolicyDraft? draft,
        bool confirmRejudge,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Changes the quant-fidelity settings of a project that may already be frozen, and optionally queues a
    ///     measurement for every succeeded cell that has none. A base-model or chunk-count change mints a new expected
    ///     comparability digest, which makes previously stored KLD figures read as stale — no attempt is deleted or
    ///     rewritten.
    /// </summary>
    Task<BenchmarkProjectFidelityChange> UpdateFidelityAsync(Guid projectId,
        long expectedVersion,
        BenchmarkProjectFidelitySettings settings,
        bool measureExisting = false,
        CancellationToken cancellationToken = default);

    /// <summary>Judges one succeeded run again under the project's current policy.</summary>
    Task<BenchmarkJudgeAttemptRecord> RejudgeRunAsync(Guid runId, long expectedRunVersion, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Moves the whole project's rank cohort to the current judge runtime: resets the cohort and re-judges every
    ///     succeeded run, never a subset.
    /// </summary>
    Task<BenchmarkJudgePolicyChange> RejudgeProjectAsync(Guid projectId, long expectedProjectVersion, CancellationToken cancellationToken = default);
}

/// <param name="EnqueuedRunIds">The runs a judging was queued for, in the order they were enqueued. Empty on a no-op.</param>
public sealed record BenchmarkJudgePolicyChange(
    BenchmarkProjectRecord Project,
    IReadOnlyList<Guid> EnqueuedRunIds,
    int? CohortGeneration);

public sealed class BenchmarkProjectService(
    IBenchmarkStore benchmarkStore,
    IAgentDefinitionStore agentDefinitionStore,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    IBenchmarkJudgeRuntimeResolver judgeRuntimeResolver,
    IBenchmarkCatalogService catalog,
    IBenchmarkQueueSignal? queueSignal = null,
    IBenchmarkPairwisePlanner? pairwisePlanner = null) : IBenchmarkProjectService
{
    private readonly IBenchmarkCatalogService _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    private readonly IBenchmarkStore _benchmarkStore = benchmarkStore ?? throw new ArgumentNullException(nameof(benchmarkStore));
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels = installedModels ?? throw new ArgumentNullException(nameof(installedModels));

    private readonly IBenchmarkJudgeRuntimeResolver _judgeRuntimeResolver =
        judgeRuntimeResolver ?? throw new ArgumentNullException(nameof(judgeRuntimeResolver));

    private readonly IBenchmarkQueueSignal? _queueSignal = queueSignal;
    private readonly IBenchmarkPairwisePlanner? _pairwisePlanner = pairwisePlanner;

    public async Task<BenchmarkProjectRecord> CreateAsync(BenchmarkProjectDraft draft, CancellationToken cancellationToken = default)
    {
        var (input, policy) = await ValidateAsync(draft, cancellationToken).ConfigureAwait(false);

        // Project, judge AND item 0 in one store call, so a failure between them cannot persist a project with
        // judging off — or with no question to ask — that the operator could only retry into a duplicate. Every
        // project created from here therefore has its items already; the lazy backfill is left for older rows only.
        return await _benchmarkStore.CreateProjectAsync(input,
                                        ToPolicyChange(policy),
                                        [new BenchmarkTaskItemInput(input.CoreTaskJson)],
                                        cancellationToken)
                                    .ConfigureAwait(false);
    }

    public async Task<BenchmarkProjectRecord> UpdateAsync(Guid projectId,
        long expectedVersion,
        BenchmarkProjectDraft draft,
        CancellationToken cancellationToken = default)
    {
        var (input, policy) = await ValidateAsync(draft with
        {
            Id = projectId
        }, cancellationToken).ConfigureAwait(false);

        // This route changes the rubric too, so it owes the item overrides the same check the judge-only route makes.
        if (policy is not null)
        {
            await EnsureItemOverridesFitAsync(projectId, policy.Rubric, cancellationToken).ConfigureAwait(false);
        }

        // An unfrozen project edits its judge exactly the way a frozen one does — get-or-create plus repoint, never an
        // in-place edit of a revision — minus the re-judge, because it has no runs to re-judge. Both halves commit
        // together: an edit that lost its judge change would leave the project judging under the replaced policy.
        return await _benchmarkStore.UpdateProjectAsync(projectId,
                                        expectedVersion,
                                        input,
                                        ToPolicyChange(policy) ?? BenchmarkJudgePolicyChangeInput.Disabled,
                                        cancellationToken)
                                    .ConfigureAwait(false);
    }

    public async Task<BenchmarkProjectFidelityChange> UpdateFidelityAsync(Guid projectId,
        long expectedVersion,
        BenchmarkProjectFidelitySettings settings,
        bool measureExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var baseModelName = NormalizeModelName(settings.KldBaseModelName);
        var change = await _benchmarkStore.UpdateProjectFidelityAsync(projectId,
                                              expectedVersion,
                                              new BenchmarkProjectFidelityInput(settings.Enabled,
                                                  settings.KldEnabled,
                                                  settings.Chunks,
                                                  baseModelName,
                                                  await ResolveKldBaseFingerprintAsync(settings.KldEnabled, settings.Chunks, baseModelName, cancellationToken)
                                                      .ConfigureAwait(false)),
                                              measureExisting,
                                              cancellationToken)
                                          .ConfigureAwait(false);
        if (change.EnqueuedRunIds.Count > 0)
        {
            _queueSignal?.Wake();
        }

        return change;
    }

    public async Task<BenchmarkJudgePolicyChange> UpdateJudgePolicyAsync(Guid projectId,
        long expectedVersion,
        BenchmarkJudgePolicyDraft? draft,
        bool confirmRejudge,
        CancellationToken cancellationToken = default)
    {
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project.Version != expectedVersion)
        {
            throw new BenchmarkConflictException("VersionConflict");
        }

        var current = await _benchmarkStore.GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);

        // Changing the judge invalidates every score already given under the old one. The operator confirms that
        // explicitly rather than discovering a silently re-scored project.
        //
        // Both answers are given BEFORE the policy is built, because building it takes the VERIFYING model lease,
        // which re-hashes every member file: a 22 GB judge made this refusal take 57 s to say no.
        if (draft is not null && !confirmRejudge && await _benchmarkStore.CountRunsAsync(projectId, cancellationToken).ConfigureAwait(false) > 0)
        {
            if (MatchesCurrentPolicy(draft, current))
            {
                return new BenchmarkJudgePolicyChange(project, [], current!.CohortGeneration);
            }

            throw new BenchmarkConflictException("RejudgeRequired");
        }

        var policy = draft is null ? null : await BuildPolicyAsync(draft, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            if (current is null)
            {
                return new BenchmarkJudgePolicyChange(project, [], null);
            }

            await _benchmarkStore.DisableJudgePolicyAsync(projectId, expectedVersion, cancellationToken).ConfigureAwait(false);
            return new BenchmarkJudgePolicyChange(await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false), [], null);
        }

        var hash = BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy);
        if (current is not null && string.Equals(current.PolicyHash, hash, StringComparison.Ordinal))
        {
            return new BenchmarkJudgePolicyChange(project, [], current.CohortGeneration);
        }

        await EnsureItemOverridesFitAsync(projectId, policy.Rubric, cancellationToken).ConfigureAwait(false);
        var activation = await _benchmarkStore.ActivateJudgePolicyAsync(projectId,
                                                  expectedVersion,
                                                  new ReadOnlyMemory<byte>(BenchmarkJudgeSerialization.SerializePolicy(policy)),
                                                  hash,
                                                  await BuildCohortSeedAsync(policy, expectedRevisionId: null, cancellationToken).ConfigureAwait(false),
                                                  cancellationToken)
                                              .ConfigureAwait(false);
        return await WakeAndDescribeAsync(await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false), activation, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BenchmarkJudgeAttemptRecord> RejudgeRunAsync(Guid runId,
        long expectedRunVersion,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var run = await _benchmarkStore.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
        var revision = await _benchmarkStore.GetCurrentJudgePolicyRevisionAsync(run.ProjectId, cancellationToken).ConfigureAwait(false)
                       ?? throw new BenchmarkConflictException("JudgeDisabled");
        var policy = BenchmarkJudgeSerialization.DeserializePolicy(revision.PolicyJson!.Value.Span);
        var resolved = await TryResolveRuntimeAsync(policy, cancellationToken).ConfigureAwait(false);
        var attempt = await _benchmarkStore.EnqueueJudgeAttemptAsync(new BenchmarkEnqueueJudgeAttemptCommand(runId,
                                               expectedRunVersion,
                                               revision.Id,
                                               resolved.RuntimeJson,
                                               resolved.UnresolvedReason,
                                               force,
                                               resolved.Intent), cancellationToken)
                                           .ConfigureAwait(false);
        _queueSignal?.Wake();
        return attempt;
    }

    public async Task<BenchmarkJudgePolicyChange> RejudgeProjectAsync(Guid projectId,
        long expectedProjectVersion,
        CancellationToken cancellationToken = default)
    {
        // The runtime is resolved BEFORE the store call so the reset and every attempt land in one transaction. The
        // revision it was resolved for is carried along: a project that moved on meanwhile rolls the whole thing back.
        var revision = await _benchmarkStore.GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken).ConfigureAwait(false)
                       ?? throw new BenchmarkConflictException("JudgeDisabled");
        var policy = BenchmarkJudgeSerialization.DeserializePolicy(revision.PolicyJson!.Value.Span);
        var activation = await _benchmarkStore.BeginProjectRejudgeAsync(projectId,
                                                  expectedProjectVersion,
                                                  await BuildCohortSeedAsync(policy, revision.Id, cancellationToken).ConfigureAwait(false),
                                                  cancellationToken)
                                              .ConfigureAwait(false);
        return await WakeAndDescribeAsync(await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false), activation, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string DecodeCoreTask(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(payload)
                   ?? throw new BenchmarkValidationException("The benchmark task is required.");
        }
        catch (JsonException exception)
        {
            throw new BenchmarkValidationException($"The benchmark task payload is invalid: {exception.Message}");
        }
    }

    /// <summary>
    ///     Resolves the judge runtime ONCE for the revision, for the store to seed every eligible run's attempt with.
    ///     The runtime depends only on the policy, so resolving it per run would repeat identical work and could
    ///     straddle a runtime swap mid-loop, splitting one re-judge across two cohorts.
    ///     <para>
    ///         A PAIRWISE policy seeds no attempt at all. Its cohort is judged by the comparisons
    ///         <see cref="IBenchmarkPairwisePlanner.EnsurePairsAsync" /> plans immediately after this activation, so a
    ///         pointwise attempt per run would queue a second judging of every run that the mode never asked for — and
    ///         the planner resolves the runtime itself, which is why this does not even take the verifying lease. The
    ///         seed is still returned, because it is what pins the revision the caller resolved against.
    ///     </para>
    /// </summary>
    private async Task<BenchmarkJudgeAttemptSeed> BuildCohortSeedAsync(BenchmarkJudgePolicyV1 policy,
        Guid? expectedRevisionId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(BenchmarkJudgePolicyModes.Normalize(policy.Mode), BenchmarkJudgePolicyModes.Pairwise, StringComparison.Ordinal))
        {
            return new BenchmarkJudgeAttemptSeed(expectedRevisionId, SeedPointwiseAttempts: false);
        }

        var resolved = await TryResolveRuntimeAsync(policy, cancellationToken).ConfigureAwait(false);
        return new BenchmarkJudgeAttemptSeed(expectedRevisionId, resolved.RuntimeJson, resolved.UnresolvedReason, resolved.Intent);
    }

    /// <summary>Wakes the queue for the attempts the store just enqueued and reports them to the caller.</summary>
    private async Task<BenchmarkJudgePolicyChange> WakeAndDescribeAsync(BenchmarkProjectRecord project,
        BenchmarkJudgePolicyActivation activation,
        CancellationToken cancellationToken)
    {
        // Activation is the FIRST of the three places a pairwise cohort grows: switching a project to pairwise must
        // seed its comparisons now, not at the next primary success or the next restart. A no-op in pointwise mode.
        if (_pairwisePlanner is not null)
        {
            _ = await _pairwisePlanner.EnsurePairsAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }

        if (activation.SucceededRunIds.Count > 0)
        {
            _queueSignal?.Wake();
        }

        return new BenchmarkJudgePolicyChange(project, activation.SucceededRunIds, activation.Revision.CohortGeneration);
    }

    private static BenchmarkJudgePolicyChangeInput? ToPolicyChange(BenchmarkJudgePolicyV1? policy) =>
        policy is null
            ? null
            : new BenchmarkJudgePolicyChangeInput(new ReadOnlyMemory<byte>(BenchmarkJudgeSerialization.SerializePolicy(policy)),
                BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy));

    /// <summary>
    ///     The judge runtime, or the sanitized reason it could not be resolved. A resolution failure becomes a failed
    ///     attempt, never a refused activation: the operator's policy is valid, this node's runtime is not.
    /// </summary>
    private async Task<ResolvedJudgeRuntime> TryResolveRuntimeAsync(BenchmarkJudgePolicyV1 policy, CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _judgeRuntimeResolver.ResolveAsync(policy, cancellationToken).ConfigureAwait(false);
            return new ResolvedJudgeRuntime(new ReadOnlyMemory<byte>(BenchmarkJudgeSerialization.SerializeRuntime(resolution.Runtime)),
                null,
                resolution.Intent);
        }
        catch (Exception exception) when (exception is BenchmarkEligibilityException
                                              or BenchmarkUnsupportedKvCacheTypeException
                                              or BenchmarkSnapshotException
                                              or KeyNotFoundException)
        {
            return new ResolvedJudgeRuntime(null, exception.Message, null);
        }
    }

    private async Task<BenchmarkProjectRecord> RequireProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await _benchmarkStore.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");

    private async Task<(BenchmarkProjectInput Input, BenchmarkJudgePolicyV1? Policy)> ValidateAsync(BenchmarkProjectDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.CoreTask))
        {
            throw new BenchmarkValidationException("Benchmark name and task are required.");
        }

        ValidateContext(draft.ContextTokens, "primary");
        ValidateOutputBudget(draft.MaxOutputTokens, draft.ContextTokens);
        ValidateReasoningBudget(draft.ReasoningBudgetTokens, draft.MaxOutputTokens, draft.ContextTokens);
        ValidateInvocationTimeout(draft.InvocationTimeoutSeconds);
        var definition = await _agentDefinitionStore.GetByIdAsync(draft.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.Kind != AgentDefinitionKind.Single)
        {
            throw new BenchmarkValidationException("An existing Single agent definition is required.");
        }

        var baseFingerprint = await ResolveKldBaseFingerprintAsync(draft.FidelityKldEnabled,
                draft.FidelityChunks,
                NormalizeModelName(draft.FidelityKldBaseModelName),
                cancellationToken)
            .ConfigureAwait(false);
        var policy = draft.Judge is null ? null : await BuildPolicyAsync(draft.Judge, cancellationToken).ConfigureAwait(false);
        return (new BenchmarkProjectInput(draft.Id,
                draft.Name.Trim(),
                JsonSerializer.SerializeToUtf8Bytes(draft.CoreTask),
                draft.ContextTokens,
                draft.AgentDefinitionId,
                draft.MaxOutputTokens,
                draft.InvocationTimeoutSeconds,
                draft.ReasoningBudgetTokens,
                draft.FidelityEnabled,
                draft.FidelityKldEnabled,
                draft.FidelityChunks,
                NormalizeModelName(draft.FidelityKldBaseModelName),
                baseFingerprint),
            policy);
    }

    /// <summary>
    ///     The base model's content fingerprint, read from the eligible-model catalog rather than taken from the
    ///     caller. Two reasons it is not client-writable: it is an input to the KLD comparability digest, so a wrong
    ///     value would make incomparable numbers compare equal; and resolving it here is what proves the named model
    ///     is an eligible local GGUF at all.
    ///     <para>
    ///         The catalog reads recorded registry facts without re-hashing every member file, which is why selecting
    ///         a 25 GB base model does not cost a minute of hashing on save. The fidelity executor re-verifies the
    ///         fingerprint against a verifying lease before it measures anything, so a file swapped under an unchanged
    ///         name fails there rather than being silently measured.
    ///     </para>
    /// </summary>
    private async Task<string?> ResolveKldBaseFingerprintAsync(bool kldEnabled,
        int? chunks,
        string? baseModelName,
        CancellationToken cancellationToken)
    {
        ValidateFidelityChunks(chunks);
        if (baseModelName is null)
        {
            if (kldEnabled)
            {
                throw new BenchmarkValidationException("KL divergence requires a base model.");
            }

            return null;
        }

        var models = await _catalog.ListEligibleModelsAsync(contextTokens: null, cancellationToken).ConfigureAwait(false);
        return models.FirstOrDefault(model => string.Equals(model.ModelName, baseModelName, StringComparison.Ordinal))?.ModelContentFingerprint
               ?? throw new BenchmarkValidationException("The KL-divergence base model is not an eligible local model.");
    }

    private static void ValidateFidelityChunks(int? chunks)
    {
        if (chunks is { } value && value is < BenchmarkFidelityPolicy.MinimumChunks or > BenchmarkFidelityPolicy.MaximumChunks)
        {
            throw new BenchmarkValidationException($"The fidelity chunk count must be between {BenchmarkFidelityPolicy.MinimumChunks} and {BenchmarkFidelityPolicy.MaximumChunks}.");
        }
    }

    private static string? NormalizeModelName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    ///     Builds the hashable policy from the operator's draft: the judge model's identity as installed right now, the
    ///     deterministic sampling every judging replays, and the rubric.
    /// </summary>
    /// <summary>
    ///     Whether a draft would rebuild the policy the project already carries, decided WITHOUT verifying the model.
    ///     Everything a policy hashes over is here except the model's content fingerprint and member hashes, which only
    ///     the verifying lease can produce — so the stored identity is reused for those and the model NAME is compared
    ///     on its own. The comparison is the real canonicalizer, not a field-by-field re-implementation: a member added
    ///     to the policy is then covered here the moment it enters the hash.
    ///     <para>
    ///         Ceiling, and the honest outcome: a judge model whose FILE changed on disk under an unchanged name reads
    ///         as unchanged here, so the re-save is a no-op instead of a re-judge. The verifying path still detects it
    ///         the next time the policy is actually built — the same answer, one save later.
    ///     </para>
    /// </summary>
    private static bool MatchesCurrentPolicy(BenchmarkJudgePolicyDraft draft, BenchmarkJudgePolicyRevisionRecord? current)
    {
        if (current?.PolicyJson is null)
        {
            return false;
        }

        BenchmarkJudgePolicyV1 stored;
        try
        {
            stored = BenchmarkJudgeSerialization.DeserializePolicy(current.PolicyJson.Value.Span);
        }
        catch (Exception exception) when (exception is BenchmarkSnapshotException or BenchmarkJudgePolicyValidationException)
        {
            // A revision that cannot be read or no longer validates cannot be shown to match anything; it falls
            // through to the confirmation, and the verifying path decides.
            return false;
        }

        if (!string.Equals(stored.Model.ModelName, draft.ModelName?.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = new BenchmarkJudgePolicyV1(stored.Model,
            draft.ContextTokens,
            BenchmarkJudgePolicyVersions.PromptVersion,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            draft.Rubric ?? BenchmarkJudgeRubricDefaults.Default(),
            NormalizeReferenceAnswer(draft.ReferenceAnswer),
            BenchmarkJudgePolicyModes.Normalize(draft.Mode));
        return string.Equals(BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(candidate), current.PolicyHash, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Refuses a rubric that would strand an item's verifier override — one naming a criterion the new rubric
    ///     drops, renames, or gives an incompatible kind.
    ///     <para>
    ///         The alternative was to accept the rubric and mark the affected items revised. It was rejected as the
    ///         larger and less honest option: a stranded override is not a stale answer to a question that moved, it is
    ///         a question with no expected answer at all, and quietly unranking the item hides an edit the operator can
    ///         still take back. Refusing names both halves of the fix — clear the item's override, or keep the
    ///         criterion — and costs one read of an at-most-20-row table.
    ///     </para>
    /// </summary>
    private async Task EnsureItemOverridesFitAsync(Guid projectId, BenchmarkJudgeRubricV1 rubric, CancellationToken cancellationToken)
    {
        foreach (var item in await _benchmarkStore.ListTaskItemsAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            if (item.VerifierConfigJson is not { IsEmpty: false } config)
            {
                continue;
            }

            try
            {
                BenchmarkTaskItemService.EnsureOverridesFitRubric(config, rubric);
            }
            catch (BenchmarkValidationException exception)
            {
                throw new BenchmarkValidationException($"Task item {item.Index + 1} cannot be judged under this rubric. {exception.Message}")
                {
                    Source = exception.Source
                };
            }
        }
    }

    private async Task<BenchmarkJudgePolicyV1> BuildPolicyAsync(BenchmarkJudgePolicyDraft draft, CancellationToken cancellationToken)
    {
        var modelName = draft.ModelName?.Trim();
        if (string.IsNullOrWhiteSpace(modelName) || draft.ContextTokens <= 0)
        {
            throw new BenchmarkValidationException("An enabled judge requires a local model and context.");
        }

        ValidateContext(draft.ContextTokens, "judge");
        try
        {
            await using var lease = await _installedModels.AcquireAsync(modelName, cancellationToken).ConfigureAwait(false);
            BenchmarkModelEligibility.ValidateJudge(lease.Snapshot);
            var policy = new BenchmarkJudgePolicyV1(BenchmarkJudgePolicyModelV1.FromSnapshot(BenchmarkInstalledModelSnapshotMapper.ToSnapshot(lease.Snapshot)),
                draft.ContextTokens,
                BenchmarkJudgePolicyVersions.PromptVersion,
                BenchmarkJudgePolicyVersions.OutputSchemaVersion,
                BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
                draft.Rubric ?? BenchmarkJudgeRubricDefaults.Default(),
                NormalizeReferenceAnswer(draft.ReferenceAnswer),
                BenchmarkJudgePolicyModes.Normalize(draft.Mode));
            BenchmarkJudgePolicyValidator.Validate(policy);
            return policy;
        }
        catch (KeyNotFoundException exception)
        {
            throw new BenchmarkValidationException("The selected judge model is not installed or eligible.")
            {
                Source = exception.Source
            };
        }
        catch (BenchmarkEligibilityException exception)
        {
            throw new BenchmarkValidationException(exception.Message)
            {
                Source = exception.Source
            };
        }
        catch (BenchmarkJudgePolicyValidationException exception)
        {
            throw new BenchmarkValidationException(exception.Message)
            {
                Source = exception.Source
            };
        }
    }

    private static string? NormalizeReferenceAnswer(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    ///     The output budget must leave room for the prompt: a budget at or above the context window cannot be honoured
    ///     and would silently behave like no budget at all. Absent means context-limited, which is the default.
    /// </summary>
    private static void ValidateOutputBudget(int? maxOutputTokens, int contextTokens)
    {
        if (maxOutputTokens is { } budget && (budget < 1 || budget >= contextTokens))
        {
            throw new BenchmarkValidationException("The output token budget must be between 1 and the requested context, exclusive.");
        }
    }

    /// <summary>
    ///     The reasoning budget must leave room for an answer. On its own it is bounded like the output budget; with an
    ///     output budget ALSO pinned the two are additive inside one window, and a pair that sums past the context is a
    ///     project that can only ever produce truncated runs — the model spends the budget thinking, hits the ceiling,
    ///     and every run is excluded from its own ranking. A coarse prompt reserve is included because the task and the
    ///     system prompt occupy the same window and are not zero.
    /// </summary>
    private static void ValidateReasoningBudget(int? reasoningBudgetTokens, int? maxOutputTokens, int contextTokens)
    {
        if (reasoningBudgetTokens is not { } budget)
        {
            return;
        }

        if (budget < 1 || budget >= contextTokens)
        {
            throw new BenchmarkValidationException("The reasoning token budget must be between 1 and the requested context, exclusive.");
        }

        if (maxOutputTokens is { } output && BenchmarkFrozenPolicies.MinimumPromptReserveTokens + budget + output > contextTokens)
        {
            throw new BenchmarkValidationException($"The reasoning and output token budgets must leave at least "
                                                   + $"{BenchmarkFrozenPolicies.MinimumPromptReserveTokens} tokens of the requested context for the prompt.");
        }
    }

    /// <summary>
    ///     The generation budget must be a plausible one. The floor keeps a typo from cancelling every run before the
    ///     model warms; the ceiling keeps a runaway run from occupying the queue for a day.
    /// </summary>
    private static void ValidateInvocationTimeout(int? invocationTimeoutSeconds)
    {
        if (invocationTimeoutSeconds is { } seconds
            && (seconds < BenchmarkFrozenPolicies.MinInvocationTimeoutSeconds || seconds > BenchmarkFrozenPolicies.MaxInvocationTimeoutSeconds))
        {
            throw new BenchmarkValidationException($"The generation timeout must be between {BenchmarkFrozenPolicies.MinInvocationTimeoutSeconds} and "
                                                   + $"{BenchmarkFrozenPolicies.MaxInvocationTimeoutSeconds} seconds.");
        }
    }

    private static void ValidateContext(int contextTokens, string role)
    {
        if (!LlamaServerLaunchPolicyOptions.ChatContextTiers.Contains(contextTokens))
        {
            throw new BenchmarkValidationException($"The {role} context budget is not supported.");
        }
    }

    private sealed record ResolvedJudgeRuntime(ReadOnlyMemory<byte>? RuntimeJson, string? UnresolvedReason, BenchmarkRunLaunchIntent? Intent);
}
