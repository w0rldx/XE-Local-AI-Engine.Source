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
    IBenchmarkQueueSignal? queueSignal = null) : IBenchmarkProjectService
{
    private readonly IBenchmarkStore _benchmarkStore = benchmarkStore ?? throw new ArgumentNullException(nameof(benchmarkStore));
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels = installedModels ?? throw new ArgumentNullException(nameof(installedModels));

    private readonly IBenchmarkJudgeRuntimeResolver _judgeRuntimeResolver =
        judgeRuntimeResolver ?? throw new ArgumentNullException(nameof(judgeRuntimeResolver));

    private readonly IBenchmarkQueueSignal? _queueSignal = queueSignal;

    public async Task<BenchmarkProjectRecord> CreateAsync(BenchmarkProjectDraft draft, CancellationToken cancellationToken = default)
    {
        var (input, policy) = await ValidateAsync(draft, cancellationToken).ConfigureAwait(false);

        // Project and judge in one store call, so a failure between them cannot persist a project with judging off
        // that the operator could only retry into a duplicate.
        return await _benchmarkStore.CreateProjectAsync(input, ToPolicyChange(policy), cancellationToken).ConfigureAwait(false);
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

        var policy = draft is null ? null : await BuildPolicyAsync(draft, cancellationToken).ConfigureAwait(false);
        var current = await _benchmarkStore.GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
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

        // Changing the judge invalidates every score already given under the old one. The operator confirms that
        // explicitly rather than discovering a silently re-scored project.
        if (!confirmRejudge && await _benchmarkStore.CountRunsAsync(projectId, cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new BenchmarkConflictException("RejudgeRequired");
        }

        var activation = await _benchmarkStore.ActivateJudgePolicyAsync(projectId,
                                                  expectedVersion,
                                                  new ReadOnlyMemory<byte>(BenchmarkJudgeSerialization.SerializePolicy(policy)),
                                                  hash,
                                                  await BuildCohortSeedAsync(policy, expectedRevisionId: null, cancellationToken).ConfigureAwait(false),
                                                  cancellationToken)
                                              .ConfigureAwait(false);
        return WakeAndDescribe(await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false), activation);
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
        return WakeAndDescribe(await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false), activation);
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
    /// </summary>
    private async Task<BenchmarkJudgeAttemptSeed> BuildCohortSeedAsync(BenchmarkJudgePolicyV1 policy,
        Guid? expectedRevisionId,
        CancellationToken cancellationToken)
    {
        var resolved = await TryResolveRuntimeAsync(policy, cancellationToken).ConfigureAwait(false);
        return new BenchmarkJudgeAttemptSeed(expectedRevisionId, resolved.RuntimeJson, resolved.UnresolvedReason, resolved.Intent);
    }

    /// <summary>Wakes the queue for the attempts the store just enqueued and reports them to the caller.</summary>
    private BenchmarkJudgePolicyChange WakeAndDescribe(BenchmarkProjectRecord project, BenchmarkJudgePolicyActivation activation)
    {
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
        var definition = await _agentDefinitionStore.GetByIdAsync(draft.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.Kind != AgentDefinitionKind.Single)
        {
            throw new BenchmarkValidationException("An existing Single agent definition is required.");
        }

        var policy = draft.Judge is null ? null : await BuildPolicyAsync(draft.Judge, cancellationToken).ConfigureAwait(false);
        return (new BenchmarkProjectInput(draft.Id,
                draft.Name.Trim(),
                JsonSerializer.SerializeToUtf8Bytes(draft.CoreTask),
                draft.ContextTokens,
                draft.AgentDefinitionId),
            policy);
    }

    /// <summary>
    ///     Builds the hashable policy from the operator's draft: the judge model's identity as installed right now, the
    ///     deterministic sampling every judging replays, and the rubric.
    /// </summary>
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
                NormalizeReferenceAnswer(draft.ReferenceAnswer));
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

    private static void ValidateContext(int contextTokens, string role)
    {
        if (!LlamaServerLaunchPolicyOptions.ChatContextTiers.Contains(contextTokens))
        {
            throw new BenchmarkValidationException($"The {role} context budget is not supported.");
        }
    }

    private sealed record ResolvedJudgeRuntime(ReadOnlyMemory<byte>? RuntimeJson, string? UnresolvedReason, BenchmarkRunLaunchIntent? Intent);
}
