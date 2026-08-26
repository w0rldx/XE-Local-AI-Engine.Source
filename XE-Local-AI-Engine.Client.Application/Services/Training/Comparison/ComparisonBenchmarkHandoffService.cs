namespace XE_Local_AI_Engine.Client.Services.Training.Comparison;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     What the operator asked to benchmark. The <paramref name="CoreTask" /> is REQUIRED and comes from the operator:
///     a comparison's evaluation prompt is a scoring-harness input, not a benchmark task, and silently reusing it would
///     produce a benchmark of the wrong thing.
/// </summary>
public sealed record CreateBenchmarkFromComparisonCommand(
    Guid ComparisonId,
    string CoreTask,
    int ContextTokens,
    Guid AgentDefinitionId,
    string? Name = null,
    string? KvCacheType = null,
    int RepeatCount = 1,
    bool Warmup = false);

/// <param name="BaseRunIds">The base model's runs, in the order they were enqueued. The tuned group follows them.</param>
public sealed record ComparisonBenchmarkHandoff(Guid ProjectId,
    string BaseModelName,
    string TunedModelName,
    IReadOnlyList<Guid> BaseRunIds,
    IReadOnlyList<Guid> TunedRunIds);

public interface IComparisonBenchmarkHandoffService
{
    /// <summary>
    ///     Creates (or reuses) the benchmark project for one training comparison and enqueues the paired base/tuned runs
    ///     against it.
    /// </summary>
    Task<ComparisonBenchmarkHandoff> CreateAsync(CreateBenchmarkFromComparisonCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
///     Closes the one-way gap the old training → benchmark deep link left: it could only SELECT runs that already
///     existed in a project the operator had already built, so a freshly trained model had nothing to open. This creates
///     the project and starts both runs in one action.
///     <para>
///         <b>Paired by construction.</b> Both models are frozen against the same project (same task, same context, same
///         agent), with the same KV-cache type and the same repeat count, through one shared
///         <see cref="BenchmarkFreezeScope" /> — so the two sides differ in the model and nothing else, which is the only
///         condition under which their scores may be subtracted.
///     </para>
///     <para>
///         <b>Both sides or neither.</b> The pair is frozen — resolved, verified, checked against the project version —
///         on both sides before a single run is written, and then inserted in ONE all-or-nothing commit. A failure on
///         the tuned side therefore leaves nothing queued, so a retry cannot duplicate the base group.
///     </para>
///     <para>
///         <b>Installed models only.</b> A comparison's tuned side is usually a STAGED artifact, which the benchmark
///         harness cannot launch. The tuned model must have been promoted into the local registry first (which stamps it
///         <see cref="Providers.Abstractions.Contracts.LocalModelOrigin.Trained" /> and gives it the installed name used
///         here); until then the hand-off is refused with that reason rather than failing later inside the freeze.
///     </para>
/// </summary>
public sealed class ComparisonBenchmarkHandoffService(
    ITrainingEvaluationStore evaluations,
    ITrainingRunStore runs,
    IBenchmarkStore benchmarks,
    IBenchmarkProjectService projects,
    IBenchmarkRunFreezeService freeze) : IComparisonBenchmarkHandoffService
{
    private readonly IBenchmarkStore _benchmarks = benchmarks ?? throw new ArgumentNullException(nameof(benchmarks));
    private readonly ITrainingEvaluationStore _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
    private readonly IBenchmarkRunFreezeService _freeze = freeze ?? throw new ArgumentNullException(nameof(freeze));
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly ITrainingRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public async Task<ComparisonBenchmarkHandoff> CreateAsync(CreateBenchmarkFromComparisonCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.CoreTask))
        {
            throw new BenchmarkValidationException(
                "A benchmark task is required. A comparison's evaluation prompt scores the hold-out samples; it is not the task to benchmark the two models on.");
        }

        if (command.RepeatCount is < 1 or > 10)
        {
            throw new BenchmarkValidationException("The repeat count must be between 1 and 10.");
        }

        if (!BenchmarkKvCacheType.TryNormalize(command.KvCacheType, out var kvCacheType))
        {
            throw new BenchmarkValidationException("The requested KV-cache type is not supported.");
        }

        var comparison = await _evaluations.GetComparisonAsync(command.ComparisonId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BenchmarkNotFoundException("The training comparison was not found.");

        var baseModelName = await ResolveInstalledModelNameAsync(comparison.BaseEvaluationRunId, "base", cancellationToken).ConfigureAwait(false);
        var tunedModelName = await ResolveInstalledModelNameAsync(comparison.TunedEvaluationRunId, "tuned", cancellationToken).ConfigureAwait(false);
        if (string.Equals(baseModelName, tunedModelName, StringComparison.Ordinal))
        {
            // Both sides resolving to one installed name means the tuned artifact was promoted over the base entry (or
            // neither side was promoted). Two runs of the same model are not a comparison, so say so here rather than
            // enqueue an hour of GPU time that answers nothing.
            throw new BenchmarkValidationException(
                "The base and tuned sides of this comparison resolve to the same installed model, so there is nothing to compare. Register the tuned artifact under its own model name first.");
        }

        var name = string.IsNullOrWhiteSpace(command.Name) ? comparison.Name : command.Name.Trim();
        var project = await GetOrCreateProjectAsync(name, command, cancellationToken).ConfigureAwait(false);

        // One scope for the pair, exactly as the matrix batch does: one capability probe, one verified lease per model,
        // and the lease held so the tuned side cannot be frozen against different bytes than the base side was.
        await using var scope = new BenchmarkFreezeScope();

        // BOTH sides are decided — model resolved and verified, eligibility applied, project version checked — before
        // EITHER is written. Committing the base group first meant a tuned side that failed any of those checks left
        // an hour of base runs queued while the caller got an error carrying no ids, so the only retry available
        // queued a SECOND base group. One commit and one compare-and-swap: on any failure nothing is persisted.
        var plans = new List<BenchmarkFrozenRunPlan>(2);
        foreach (var modelName in new[] { baseModelName, tunedModelName })
        {
            plans.Add(await FreezeAsync(project.Id, modelName, project.Version, kvCacheType, command, scope, cancellationToken).ConfigureAwait(false));
        }

        var started = await _freeze.CommitAsync(plans, cancellationToken).ConfigureAwait(false);
        return new ComparisonBenchmarkHandoff(project.Id,
            baseModelName,
            tunedModelName,
            [.. started[0].Select(static run => run.Id)],
            [.. started[1].Select(static run => run.Id)]);
    }

    /// <summary>
    ///     Reuses the project named for this comparison when one already exists, so re-running the hand-off after a
    ///     failed pair adds runs to the same ranking cohort instead of scattering the comparison over near-identical
    ///     projects. Matched on the exact name, the same ordinal rule the comparison report matches model names by.
    /// </summary>
    private async Task<BenchmarkProjectRecord> GetOrCreateProjectAsync(string name,
        CreateBenchmarkFromComparisonCommand command,
        CancellationToken cancellationToken)
    {
        var existing = await _benchmarks.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var match = existing.FirstOrDefault(project => string.Equals(project.Name, name, StringComparison.Ordinal));
        if (match is not null)
        {
            return match;
        }

        return await _projects.CreateAsync(new BenchmarkProjectDraft(Guid.Empty,
                                   name,
                                   command.CoreTask,
                                   command.ContextTokens,
                                   command.AgentDefinitionId), cancellationToken)
                              .ConfigureAwait(false);
    }

    /// <summary>
    ///     Freezes ONE side. Nothing is written — the pair is committed together — but every refusal a side can raise
    ///     happens here, which is what lets the message name the side's model.
    /// </summary>
    private async Task<BenchmarkFrozenRunPlan> FreezeAsync(Guid projectId,
        string modelName,
        long expectedVersion,
        string? kvCacheType,
        CreateBenchmarkFromComparisonCommand command,
        BenchmarkFreezeScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _freeze.FreezeAsync(new BenchmarkRunStartRequest(projectId,
                                     modelName,
                                     expectedVersion,
                                     kvCacheType,
                                     command.RepeatCount,
                                     command.Warmup), scope, cancellationToken)
                                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // The freeze's "no such installed model" is a bare KeyNotFoundException, which no benchmark handler maps —
            // it would escape as a 500. Here it is an operator-actionable fact about THIS comparison, so it is named.
            throw new BenchmarkValidationException($"The model '{modelName}' from this comparison is not installed on this node.");
        }
    }

    /// <summary>
    ///     The INSTALLED model name behind one side of the comparison. An evaluation that targeted an installed model
    ///     already carries it; one that targeted a staged training artifact carries the artifact's file name, which the
    ///     benchmark harness cannot launch — that side is resolved through the artifact's committed registry name and
    ///     refused when the artifact has not been registered yet.
    /// </summary>
    private async Task<string> ResolveInstalledModelNameAsync(Guid evaluationRunId, string side, CancellationToken cancellationToken)
    {
        var evaluation = await _evaluations.GetAsync(evaluationRunId, cancellationToken).ConfigureAwait(false)
                         ?? throw new BenchmarkNotFoundException($"The {side} evaluation of this comparison was not found.");

        if (evaluation.TargetKind == EvaluationModelTargetKind.InstalledModel)
        {
            return evaluation.ModelName;
        }

        if (evaluation.SourceArtifactId is not { } artifactId)
        {
            throw new BenchmarkValidationException($"The {side} evaluation scored a staged artifact that can no longer be identified, so its model cannot be benchmarked.");
        }

        var artifact = await _runs.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false)
                       ?? throw new BenchmarkValidationException($"The {side} evaluation's staged artifact no longer exists, so its model cannot be benchmarked.");
        if (string.IsNullOrWhiteSpace(artifact.CommittedModelName))
        {
            throw new BenchmarkValidationException(
                $"The {side} model of this comparison is still a staged artifact. Register it as an installed model before benchmarking it.");
        }

        return artifact.CommittedModelName;
    }
}
