namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     The operator-editable judge configuration. Everything here is inside the policy hash, so any change to it is a
///     new policy revision and — on a project that already has runs — a re-judge.
/// </summary>
/// <param name="Rubric">The weighted criteria; <see langword="null" /> takes <see cref="BenchmarkJudgeRubricDefaults.Default" />.</param>
public sealed record BenchmarkJudgePolicyDraft(
    string ModelName,
    int ContextTokens,
    BenchmarkJudgeRubricV1? Rubric = null,
    string? ReferenceAnswer = null);

public sealed record BenchmarkProjectDraft(
    Guid Id,
    string Name,
    string CoreTask,
    int ContextTokens,
    Guid AgentDefinitionId,
    BenchmarkJudgePolicyDraft? Judge = null);

public interface IBenchmarkProjectService
{
    Task<BenchmarkProjectRecord> CreateAsync(BenchmarkProjectDraft draft, CancellationToken cancellationToken = default);
    Task<BenchmarkProjectRecord> UpdateAsync(Guid projectId, long expectedVersion, BenchmarkProjectDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Changes (or, with a <see langword="null" /> draft, disables) the judge on a project that may already be
    ///     frozen. The same hash is a no-op; a different hash on a project that already has runs requires
    ///     <paramref name="confirmRejudge" /> and then re-judges every succeeded one.
    /// </summary>
    Task<BenchmarkProjectRecord> UpdateJudgePolicyAsync(Guid projectId,
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
    Task RejudgeProjectAsync(Guid projectId, long expectedProjectVersion, CancellationToken cancellationToken = default);
}

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
        var project = await _benchmarkStore.CreateProjectAsync(input, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return project;
        }

        _ = await _benchmarkStore.ActivateJudgePolicyAsync(project.Id,
                                     project.Version,
                                     BenchmarkJudgeSerialization.SerializePolicy(policy),
                                     BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy),
                                     cancellationToken)
                                 .ConfigureAwait(false);
        return await RequireProjectAsync(project.Id, cancellationToken).ConfigureAwait(false);
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
        var project = await _benchmarkStore.UpdateProjectAsync(projectId, expectedVersion, input, cancellationToken).ConfigureAwait(false);

        // An unfrozen project edits its judge exactly the way a frozen one does — get-or-create plus repoint, never an
        // in-place edit of a revision — minus the re-judge, because it has no runs to re-judge.
        return await ApplyJudgePolicyAsync(project, policy, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkProjectRecord> UpdateJudgePolicyAsync(Guid projectId,
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
                return project;
            }

            await _benchmarkStore.DisableJudgePolicyAsync(projectId, expectedVersion, cancellationToken).ConfigureAwait(false);
            return await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        var hash = BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy);
        if (current is not null && string.Equals(current.PolicyHash, hash, StringComparison.Ordinal))
        {
            return project;
        }

        // Changing the judge invalidates every score already given under the old one. The operator confirms that
        // explicitly rather than discovering a silently re-scored project.
        if (!confirmRejudge && await _benchmarkStore.CountRunsAsync(projectId, cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new BenchmarkConflictException("RejudgeRequired");
        }

        var activation = await _benchmarkStore.ActivateJudgePolicyAsync(projectId,
                                                  expectedVersion,
                                                  BenchmarkJudgeSerialization.SerializePolicy(policy),
                                                  hash,
                                                  cancellationToken)
                                              .ConfigureAwait(false);
        await EnqueueAttemptsAsync(activation, policy, cancellationToken).ConfigureAwait(false);
        return await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
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

    public async Task RejudgeProjectAsync(Guid projectId, long expectedProjectVersion, CancellationToken cancellationToken = default)
    {
        var activation = await _benchmarkStore.BeginProjectRejudgeAsync(projectId, expectedProjectVersion, cancellationToken).ConfigureAwait(false);
        var policy = BenchmarkJudgeSerialization.DeserializePolicy(activation.Revision.PolicyJson!.Value.Span);
        await EnqueueAttemptsAsync(activation, policy, cancellationToken).ConfigureAwait(false);
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
    ///     Resolves the judge runtime ONCE for the revision and enqueues one attempt per eligible run. The runtime
    ///     depends only on the policy, so resolving it per run would repeat identical work and could straddle a runtime
    ///     swap mid-loop, splitting one re-judge across two cohorts.
    /// </summary>
    private async Task EnqueueAttemptsAsync(BenchmarkJudgePolicyActivation activation,
        BenchmarkJudgePolicyV1 policy,
        CancellationToken cancellationToken)
    {
        if (activation.SucceededRunIds.Count == 0)
        {
            return;
        }

        var resolved = await TryResolveRuntimeAsync(policy, cancellationToken).ConfigureAwait(false);
        foreach (var runId in activation.SucceededRunIds)
        {
            var run = await _benchmarkStore.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                continue;
            }

            // Force: this is an activation, and the already-applied guard is a single-run idempotency rule that must
            // not exclude runs from a cohort reset (plan §3.5).
            _ = await _benchmarkStore.EnqueueJudgeAttemptAsync(new BenchmarkEnqueueJudgeAttemptCommand(runId,
                                         run.Version,
                                         activation.Revision.Id,
                                         resolved.RuntimeJson,
                                         resolved.UnresolvedReason,
                                         Force: true,
                                         resolved.Intent), cancellationToken)
                                     .ConfigureAwait(false);
        }

        _queueSignal?.Wake();
    }

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

    private async Task<BenchmarkProjectRecord> ApplyJudgePolicyAsync(BenchmarkProjectRecord project,
        BenchmarkJudgePolicyV1? policy,
        CancellationToken cancellationToken)
    {
        var current = await _benchmarkStore.GetCurrentJudgePolicyRevisionAsync(project.Id, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            if (current is null)
            {
                return project;
            }

            await _benchmarkStore.DisableJudgePolicyAsync(project.Id, project.Version, cancellationToken).ConfigureAwait(false);
            return await RequireProjectAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }

        var hash = BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy);
        if (current is not null && string.Equals(current.PolicyHash, hash, StringComparison.Ordinal))
        {
            return project;
        }

        _ = await _benchmarkStore.ActivateJudgePolicyAsync(project.Id,
                                     project.Version,
                                     BenchmarkJudgeSerialization.SerializePolicy(policy),
                                     hash,
                                     cancellationToken)
                                 .ConfigureAwait(false);
        return await RequireProjectAsync(project.Id, cancellationToken).ConfigureAwait(false);
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
            var policy = new BenchmarkJudgePolicyV1(
                BenchmarkJudgePolicyModelV1.FromSnapshot(BenchmarkInstalledModelSnapshotMapper.ToSnapshot(lease.Snapshot)),
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
