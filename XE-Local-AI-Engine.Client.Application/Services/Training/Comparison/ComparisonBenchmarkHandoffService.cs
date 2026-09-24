namespace XE_Local_AI_Engine.Client.Services.Training.Comparison;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public interface IComparisonBenchmarkHandoffService
{
    /// <summary>
    ///     Creates (or reuses) the benchmark project for one training comparison and enqueues the paired base/tuned runs
    ///     against it.
    /// </summary>
    Task<ComparisonBenchmarkHandoff> CreateAsync(CreateBenchmarkFromComparisonCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
///     Creates the benchmark project and starts both runs of a training comparison in one action, so a freshly
///     trained model has a project to open rather than only pre-existing runs to select.
/// </summary>
/// <remarks>
///     The pair is frozen against one shared <see cref="BenchmarkFreezeScope" /> and inserted in ONE all-or-nothing
///     commit, and the tuned side must already be promoted into the local registry. See docs/wiki/18-training.md
///     ("6. Evaluation and comparison").
/// </remarks>
public sealed class ComparisonBenchmarkHandoffService : IComparisonBenchmarkHandoffService
{
    private readonly IBenchmarkStore _benchmarks;
    private readonly ITrainingEvaluationStore _evaluations;
    private readonly IBenchmarkRunFreezeService _freeze;
    private readonly IBenchmarkProjectService _projects;
    private readonly ITrainingRunStore _runs;

    public ComparisonBenchmarkHandoffService(ITrainingEvaluationStore evaluations,
        ITrainingRunStore runs,
        IBenchmarkStore benchmarks,
        IBenchmarkProjectService projects,
        IBenchmarkRunFreezeService freeze)
    {
        ArgumentNullException.ThrowIfNull(benchmarks);
        ArgumentNullException.ThrowIfNull(evaluations);
        ArgumentNullException.ThrowIfNull(freeze);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(runs);
        _benchmarks = benchmarks;
        _evaluations = evaluations;
        _freeze = freeze;
        _projects = projects;
        _runs = runs;
    }

    public async Task<ComparisonBenchmarkHandoff> CreateAsync(CreateBenchmarkFromComparisonCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.CoreTask))
        {
            throw new BenchmarkValidationException("A benchmark task is required. A comparison's evaluation prompt scores the hold-out samples; it is not the task to benchmark the two models on.");
        }

        if (command.RepeatCount is < 1 or > 10)
        {
            throw new BenchmarkValidationException("The repeat count must be between 1 and 10.");
        }

        if (!BenchmarkKvCacheType.TryNormalize(command.KvCacheType, out var kvCacheType))
        {
            throw new BenchmarkValidationException("The requested KV-cache type is not supported.");
        }

        var comparison = await _evaluations.GetComparisonAsync(command.ComparisonId, cancellationToken)
                         ?? throw new BenchmarkNotFoundException("The training comparison was not found.");

        var baseModelName = await ResolveInstalledModelNameAsync(comparison.BaseEvaluationRunId, "base", cancellationToken);
        var tunedModelName = await ResolveInstalledModelNameAsync(comparison.TunedEvaluationRunId, "tuned", cancellationToken);
        if (string.Equals(baseModelName, tunedModelName, StringComparison.Ordinal))
        {
            // Both sides resolving to one installed name means the tuned artifact was promoted over the base entry, or neither
            // was. Two runs of the same model are not a comparison, so refuse here rather than queue an hour of GPU time.
            throw new BenchmarkValidationException(
                "The base and tuned sides of this comparison resolve to the same installed model, so there is nothing to compare. Register the tuned artifact under its own model name first.");
        }

        // Trimmed on both branches because that is what the project service stores, and an untrimmed name would
        // never match the project it just created.
        var name = (string.IsNullOrWhiteSpace(command.Name) ? comparison.Name : command.Name).Trim();
        var project = await GetOrCreateProjectAsync(name, command, cancellationToken);

        // One scope for the pair, exactly as the matrix batch does: one capability probe, one verified lease per model,
        // and the lease held so the tuned side cannot be frozen against different bytes than the base side was.
        await using var scope = new BenchmarkFreezeScope();

        // BOTH sides are decided — model resolved and verified, eligibility applied, project version checked — before EITHER
        // is written: one commit and one compare-and-swap, so a failure persists nothing and no retry can queue a second base group.
        var plans = new List<BenchmarkFrozenRunPlan>(2);
        foreach (var modelName in new[]
                 {
                     baseModelName,
                     tunedModelName
                 })
        {
            plans.Add(await FreezeAsync(project.Id, modelName, project.Version, kvCacheType, command, scope, cancellationToken));
        }

        var started = await _freeze.CommitAsync(plans, cancellationToken);
        return new ComparisonBenchmarkHandoff
        {
            ProjectId = project.Id,
            BaseModelName = baseModelName,
            TunedModelName = tunedModelName,
            BaseRunIds = [.. started[0].Select(static run => run.Id)],
            TunedRunIds = [.. started[1].Select(static run => run.Id)]
        };
    }

    /// <summary>
    ///     Reuses the project this comparison already has when one exists, so re-running the hand-off after a failed
    ///     pair adds runs to the same ranking cohort instead of scattering the comparison over near-identical projects.
    /// </summary>
    private async Task<BenchmarkProjectRecord> GetOrCreateProjectAsync(string name,
        CreateBenchmarkFromComparisonCommand command,
        CancellationToken cancellationToken)
    {
        var existing = await _benchmarks.ListProjectsAsync(cancellationToken);
        var match = existing.FirstOrDefault(project => IsSameBenchmark(project, name, command));
        if (match is not null)
        {
            return match;
        }

        return await _projects.CreateAsync(new BenchmarkProjectDraft
        {
            Id = Guid.Empty,
            Name = Disambiguate(name, existing),
            CoreTask = command.CoreTask,
            ContextTokens = command.ContextTokens,
            AgentDefinitionId = command.AgentDefinitionId
        }, cancellationToken);
    }

    /// <summary>
    ///     A project name is NOT an identity: names are not unique and a project carries no comparison id, so reuse
    ///     requires every field this hand-off freezes against to match as well.
    /// </summary>
    /// <remarks>
    ///     Matching on the name alone would benchmark the two models against whatever task the first project of that
    ///     name happened to hold — silently, and against a context window and an agent the operator never asked for.
    ///     The judge is deliberately NOT part of the key: the hand-off never sets one, and turning judging on
    ///     afterwards changes how a project's runs are scored, on both sides equally, not which benchmark it is.
    /// </remarks>
    private static bool IsSameBenchmark(BenchmarkProjectRecord project, string name, CreateBenchmarkFromComparisonCommand command) =>
        string.Equals(project.Name, name, StringComparison.Ordinal)
        && project.ContextTokens == command.ContextTokens
        && project.AgentDefinitionId == command.AgentDefinitionId
        // Compared as the stored bytes, the same way the store decides a core-task edit is a no-op: a decode would
        // throw on a payload written by an older shape, turning an unrelated project into a failed hand-off.
        && project.CoreTaskJson.Span.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(command.CoreTask));

    /// <summary>
    ///     A free name, suffixed when the wanted one is taken by a project that benchmarks something else. Two
    ///     comparisons sharing a name are two cohorts: merging them would rank runs that never shared a task.
    /// </summary>
    private static string Disambiguate(string name, IReadOnlyList<BenchmarkProjectRecord> existing)
    {
        var suffix = 1;
        var candidate = name;
        while (existing.Any(project => string.Equals(project.Name, candidate, StringComparison.Ordinal)))
        {
            candidate = $"{name} ({++suffix})";
        }

        return candidate;
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
            return await _freeze.FreezeAsync(new BenchmarkRunStartRequest
            {
                ProjectId = projectId,
                PrimaryModelName = modelName,
                ExpectedProjectVersion = expectedVersion,
                KvCacheType = kvCacheType,
                RepeatCount = command.RepeatCount,
                Warmup = command.Warmup
            }, scope, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            // The freeze's "no such installed model" is a bare KeyNotFoundException, which no benchmark handler maps —
            // it would escape as a 500. Here it is an operator-actionable fact about THIS comparison, so it is named.
            throw new BenchmarkValidationException($"The model '{modelName}' from this comparison is not installed on this node.");
        }
    }

    /// <summary>The INSTALLED model name behind one side of the comparison.</summary>
    /// <remarks>
    ///     An evaluation that targeted an installed model already carries it; one that targeted a staged training
    ///     artifact carries the artifact's file name, which the benchmark harness cannot launch — that side is
    ///     resolved through the artifact's committed registry name and refused when it is not registered yet.
    /// </remarks>
    private async Task<string> ResolveInstalledModelNameAsync(Guid evaluationRunId, string side, CancellationToken cancellationToken)
    {
        var evaluation = await _evaluations.GetAsync(evaluationRunId, cancellationToken)
                         ?? throw new BenchmarkNotFoundException($"The {side} evaluation of this comparison was not found.");

        if (evaluation.TargetKind == EvaluationModelTargetKind.InstalledModel)
        {
            return evaluation.ModelName;
        }

        if (evaluation.SourceArtifactId is not { } artifactId)
        {
            throw new BenchmarkValidationException($"The {side} evaluation scored a staged artifact that can no longer be identified, so its model cannot be benchmarked.");
        }

        var artifact = await _runs.GetArtifactAsync(artifactId, cancellationToken)
                       ?? throw new BenchmarkValidationException($"The {side} evaluation's staged artifact no longer exists, so its model cannot be benchmarked.");
        if (string.IsNullOrWhiteSpace(artifact.CommittedModelName))
        {
            throw new BenchmarkValidationException($"The {side} model of this comparison is still a staged artifact. Register it as an installed model before benchmarking it.");
        }

        return artifact.CommittedModelName;
    }
}
