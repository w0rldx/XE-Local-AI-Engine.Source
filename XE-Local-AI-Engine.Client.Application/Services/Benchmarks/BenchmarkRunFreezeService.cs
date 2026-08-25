namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Reflection;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     One launch request: a project, a model, and how many measured runs to enqueue against them.
/// </summary>
/// <param name="KvCacheType">
///     The KV-cache type the run asked for, or <see langword="null" /> for Auto (freeze picks). Must already be
///     canonical — see <see cref="BenchmarkKvCacheType.TryNormalize" />.
/// </param>
/// <param name="RepeatCount">
///     How many measured runs to enqueue, 1..<see cref="BenchmarkRunFreezeService.MaxRepeatCount" />. Everything but
///     the seed is frozen ONCE and the repeats share it.
/// </param>
/// <param name="Warmup">
///     Prepends one more run at repeat index 0, flagged <c>IsWarmup</c>: never ranked, never counted in a group's
///     statistics. It exists to absorb the first-launch costs (page cache cold, GPU clocks low) the measured repeats
///     should not pay.
/// </param>
/// <param name="RepeatMode">
///     What the group measures. <see cref="BenchmarkRepeatMode.Throughput" /> is the default and the historical
///     behaviour: temperature 0, one fixed seed, so the answer is identical across repeats and only the machine varies.
/// </param>
/// <param name="AnswerVarianceTemperature">
///     The temperature an <see cref="BenchmarkRepeatMode.AnswerVariance" /> group samples at, or
///     <see langword="null" /> for <see cref="BenchmarkRunFreezeService.DefaultAnswerVarianceTemperature" />. Ignored
///     in throughput mode, which is deterministic by definition.
/// </param>
public sealed record BenchmarkRunStartRequest(
    Guid ProjectId,
    string PrimaryModelName,
    long ExpectedProjectVersion,
    string? KvCacheType = null,
    int RepeatCount = 1,
    bool Warmup = false,
    BenchmarkRepeatMode RepeatMode = BenchmarkRepeatMode.Throughput,
    float? AnswerVarianceTemperature = null);

public interface IBenchmarkRunFreezeService
{
    /// <returns>
    ///     The created runs in queue order — the warm-up first when one was asked for, then repeats 1..N. Never empty.
    /// </returns>
    /// <remarks>
    ///     A repeat is a fresh <c>llama-server</c> per run, by the unchanged design of the benchmark queue: each run
    ///     claims the exclusive runtime, spawns, measures, and releases it. So repeats measure cold-launch to
    ///     cold-launch variance INCLUDING model load, not steady-state variance within one process. That is deliberate
    ///     and is what an operator comparing two models on this node actually experiences.
    ///     <para>
    ///         In <see cref="BenchmarkRepeatMode.Throughput" /> mode the frozen sampling is deterministic (temperature
    ///         0, fixed seed), so the ANSWER is the same across repeats and what they quantify is throughput jitter.
    ///         <see cref="BenchmarkRepeatMode.AnswerVariance" /> advances the SEED per repeat instead, so the runs of
    ///         one group differ in exactly one input and the spread of answers is the measurement. Either way the seed
    ///         and the temperature are sampling, never launch arguments, so every run of a group still shares one
    ///         <c>LaunchIdentity</c>.
    ///     </para>
    /// </remarks>
    Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(BenchmarkRunStartRequest request, CancellationToken cancellationToken = default);
}

public sealed class BenchmarkRunFreezeService(
    IBenchmarkStore benchmarkStore,
    IAgentDefinitionStore agentDefinitions,
    IAgentDefinitionResolver agentResolver,
    IGgufModelCapabilityResolver modelCapabilities,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    IBenchmarkEligibilityPolicy eligibilityPolicy,
    IBenchmarkFreezeDependencyService dependencies,
    IBenchmarkRuntimeSnapshotFactory snapshots,
    IBenchmarkPhaseLaunchResolver launchResolver,
    TimeProvider timeProvider,
    ILogger<BenchmarkRunFreezeService> logger,
    IBenchmarkQueueSignal? queueSignal = null) : IBenchmarkRunFreezeService
{
    private readonly ILogger<BenchmarkRunFreezeService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IBenchmarkStore _benchmarkStore = benchmarkStore ?? throw new ArgumentNullException(nameof(benchmarkStore));
    private readonly IAgentDefinitionStore _agentDefinitions = agentDefinitions ?? throw new ArgumentNullException(nameof(agentDefinitions));
    private readonly IAgentDefinitionResolver _agentResolver = agentResolver ?? throw new ArgumentNullException(nameof(agentResolver));
    private readonly IGgufModelCapabilityResolver _modelCapabilities = modelCapabilities ?? throw new ArgumentNullException(nameof(modelCapabilities));
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels = installedModels ?? throw new ArgumentNullException(nameof(installedModels));
    private readonly IBenchmarkEligibilityPolicy _eligibilityPolicy = eligibilityPolicy ?? throw new ArgumentNullException(nameof(eligibilityPolicy));
    private readonly IBenchmarkFreezeDependencyService _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IBenchmarkRuntimeSnapshotFactory _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly IBenchmarkPhaseLaunchResolver _launchResolver = launchResolver ?? throw new ArgumentNullException(nameof(launchResolver));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IBenchmarkQueueSignal? _queueSignal = queueSignal;

    /// <inheritdoc cref="BenchmarkPhaseLaunchResolver.AutoReasonCpuVariant" />
    public const string AutoReasonCpuVariant = BenchmarkPhaseLaunchResolver.AutoReasonCpuVariant;

    /// <inheritdoc cref="BenchmarkPhaseLaunchResolver.AutoReasonProbeUnavailable" />
    public const string AutoReasonProbeUnavailable = BenchmarkPhaseLaunchResolver.AutoReasonProbeUnavailable;

    /// <inheritdoc cref="BenchmarkPhaseLaunchResolver.AutoReasonManifestUnsupported" />
    public const string AutoReasonManifestUnsupported = BenchmarkPhaseLaunchResolver.AutoReasonManifestUnsupported;

    /// <inheritdoc cref="BenchmarkPhaseLaunchResolver.AutoReasonFallbackDisabled" />
    public const string AutoReasonFallbackDisabled = BenchmarkPhaseLaunchResolver.AutoReasonFallbackDisabled;

    /// <summary>The most repeats one request may enqueue. Ten cold launches of a large model is already ~an hour.</summary>
    public const int MaxRepeatCount = 10;

    /// <summary>
    ///     The temperature an answer-variance group samples at when the request pins none. 0.7 is the everyday chat
    ///     default: high enough that repeats actually diverge, low enough that the divergence is still the model
    ///     answering rather than wandering.
    /// </summary>
    public const float DefaultAnswerVarianceTemperature = 0.7f;

    /// <summary>The ceiling the chat sampling UI already enforces.</summary>
    public const float MaxAnswerVarianceTemperature = 2f;

    public async Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(BenchmarkRunStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var primaryModelName = request.PrimaryModelName;

        // A validation failure, not an ArgumentException: a blank name is one operator typo in one cell of a matrix,
        // and the endpoints' own pre-check must not be the only thing standing between it and a 500.
        if (string.IsNullOrWhiteSpace(primaryModelName))
        {
            throw new BenchmarkValidationException("A primary model is required.");
        }

        var repeatCount = request.RepeatCount;
        if (repeatCount is < 1 or > MaxRepeatCount)
        {
            throw new BenchmarkValidationException($"Repeat count must be between 1 and {MaxRepeatCount}.");
        }

        var temperature = ResolveAnswerVarianceTemperature(request);
        if (!BenchmarkKvCacheType.TryNormalize(request.KvCacheType, out var requestedKvCacheType))
        {
            throw new BenchmarkValidationException("The requested KV-cache type is not supported.");
        }

        var project = await _benchmarkStore.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                      ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");
        if (project.Version != request.ExpectedProjectVersion)
        {
            throw new BenchmarkConflictException("VersionConflict");
        }

        var warmup = request.Warmup;
        var expectedProjectVersion = request.ExpectedProjectVersion;
        var leases = new Dictionary<string, IBenchmarkInstalledModelLease>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var trimmedPrimary = primaryModelName.Trim();
            leases.Add(trimmedPrimary, await AcquireVerifiedAsync(trimmedPrimary, cancellationToken).ConfigureAwait(false));
            var primary = leases[trimmedPrimary].Snapshot;
            BenchmarkModelEligibility.Validate(primary, "primary");

            // The judge is no longer part of the freeze: its runtime is resolved per attempt, against the policy
            // revision that attempt is judged under, so a judge change never re-freezes a run.
            var definition = await _agentDefinitions.GetByIdAsync(project.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
            if (definition is null || definition.Kind != AgentDefinitionKind.Single)
            {
                throw new BenchmarkEligibilityException("The selected Single agent definition no longer exists.");
            }

            var exactCoreTask = BenchmarkProjectService.DecodeCoreTask(project.CoreTaskJson.Span);
            var capabilities = await _modelCapabilities.TryResolveAsync(primary.ModelName, cancellationToken).ConfigureAwait(false)
                               ?? throw new BenchmarkEligibilityException("The selected primary model capabilities are unavailable.");

            var resolved = await _agentResolver.ResolveAsync(project.AgentDefinitionId,
                                                   primaryModelName,
                                                   exactCoreTask,
                                                   capabilities.SupportsTools,
                                                   honorModelProfile: false,
                                                   activeModelIsCloud: false,
                                                   cancellationToken)
                                               .ConfigureAwait(false)
                           ?? throw new BenchmarkEligibilityException("The selected agent definition no longer exists.");
            var eligible = _eligibilityPolicy.Apply(resolved);
            var dependencySet = await _dependencies.CaptureAsync(project.AgentDefinitionId,
                                                       eligible,
                                                       primaryModelName,
                                                       judgeModelName: null,
                                                       cancellationToken)
                                                   .ConfigureAwait(false);
            var primarySnapshot = BenchmarkInstalledModelSnapshotMapper.ToSnapshot(primary);

            // One capability read per freeze: the primary and the judge launch the same binary, and asking twice could
            // straddle a runtime swap and freeze two different answers into one run.
            var binaryCapabilities = await _launchResolver.InspectAsync(cancellationToken).ConfigureAwait(false);

            // ONE variant for the freeze, taken from the inspection that produced the capabilities: a second selection
            // could disagree with the manifest whose digest we record.
            var variant = await _launchResolver.SelectVariantAsync(binaryCapabilities, cancellationToken).ConfigureAwait(false);
            var primaryLaunch = await _launchResolver
                                      .ResolveAsync(primary.ModelName, project.ContextTokens, requestedKvCacheType, binaryCapabilities, variant, cancellationToken)
                                      .ConfigureAwait(false);
            // The enforceability answer is frozen off the capabilities read ABOVE, not re-resolved at execution: a
            // model swap or a re-detection between freeze and run must not change what a frozen run replays.
            var primarySampling = BenchmarkFrozenPolicies.DeterministicSampling(project.MaxOutputTokens,
                project.ReasoningBudgetTokens,
                project.ReasoningBudgetTokens is null ? null : capabilities.ReasoningBudgetEnforceable);
            var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            // One snapshot per DISTINCT sampling, memoized by seed. Throughput mode has exactly one, so its group is
            // byte-for-byte the single shared payload it has always been; answer-variance mode gets one per repeat,
            // differing ONLY in the seed. Everything a launch is built from is identical either way, which is what
            // keeps a group's runs sharing one LaunchIdentity.
            var serializedSnapshots = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            byte[] SnapshotFor(BenchmarkSamplingSnapshotV1 sampling)
            {
                if (serializedSnapshots.TryGetValue(sampling.SeedValue ?? string.Empty, out var cached))
                {
                    return cached;
                }

                var created = _snapshots.Serialize(_snapshots.Create(new BenchmarkRuntimeSnapshotInput(project.Id,
                    definition.Id,
                    definition.Version,
                    exactCoreTask,
                    project.ContextTokens,
                    eligible,
                    primaryLaunch.Runtime,
                    sampling,
                    primarySnapshot,
                    dependencySet,
                    GetApplicationVersion(),
                    createdAtUtc)));
                serializedSnapshots[sampling.SeedValue ?? string.Empty] = created;
                return created;
            }

            var guard = new FreezeCommitGuard(_dependencies, dependencySet, project.AgentDefinitionId, eligible, primaryModelName,
                judgeModelName: null);

            // A group only exists when there is something to group: a plain single run keeps NULL in all three columns
            // so nothing about the old shape changes for it.
            var isGroup = repeatCount > 1 || warmup;
            var repeatGroupId = isGroup ? Guid.NewGuid() : (Guid?)null;

            // The work queue is FIFO by queue sequence, so building the commands in this order is what makes the
            // repeats run back-to-back — warm-up first, then 1..N — rather than interleaved with whatever else is
            // queued. The whole group goes in through ONE store call: a per-run insert, each chaining its
            // compare-and-swap on its predecessor, let a concurrent writer land mid-group, so the caller got a
            // conflict and no ids while the runs already inserted stayed queued and ran anyway.
            var commands = RepeatIndexes(repeatCount, warmup)
                           .Select(repeatIndex => SamplingFor(primarySampling, request.RepeatMode, temperature, repeatIndex))
                           .Select(sampling => new BenchmarkStartRunCommand(Guid.NewGuid(),
                               project.Id,
                               expectedProjectVersion,
                               SnapshotFor(sampling.Sampling),
                               primary.ModelName,
                               primary.Origin,
                               primary.ModelContentFingerprint,
                               eligible.AgentName,
                               eligible.AgentDefinitionVersion,
                               project.ContextTokens,
                               guard,
                               primaryLaunch.Intent,
                               repeatGroupId,
                               isGroup ? sampling.RepeatIndex : null,
                               warmup && sampling.RepeatIndex == 0,
                               // Copied onto the run, not read from the project at execution: a run replays with the
                               // budget it was started under, exactly like its context and its output budget.
                               project.InvocationTimeoutSeconds,
                               request.RepeatMode,
                               sampling.Sampling.SeedValue,
                               sampling.Sampling.Temperature))
                           .ToArray();
            var runs = await _benchmarkStore.StartRunsAsync(commands, expectedProjectVersion, cancellationToken).ConfigureAwait(false);

            _queueSignal?.Wake();
            return runs;
        }
        finally
        {
            foreach (var lease in leases.Values.Reverse())
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     The verifying acquire, with the one failure the freeze path owns mapped to its declared 422. Verification
    ///     moved OFF the catalog listing onto freeze, so a model whose files no longer match its registry entry now
    ///     lists happily and fails here — an unmapped <see cref="InstalledGgufSnapshotException" /> is in neither
    ///     <c>BenchmarkExceptionFilter.IsHandled</c> nor the endpoints' <see cref="KeyNotFoundException" /> clause, so
    ///     it escaped as a 500 and, in a batch, killed every cell after it instead of rejecting one. The store's own
    ///     reason is logged, never returned.
    /// </summary>
    private async Task<IBenchmarkInstalledModelLease> AcquireVerifiedAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            return await _installedModels.AcquireAsync(modelName, cancellationToken).ConfigureAwait(false);
        }
        catch (InstalledGgufSnapshotException exception)
        {
            _logger.LogWarning(exception, "Benchmark freeze: installed model {ModelName} could not be verified.", modelName);
            throw new BenchmarkEligibilityException("The selected model could not be verified against its installed registry entry.");
        }
    }

    /// <summary>
    ///     The sampling one repeat is frozen with. Throughput mode hands back the shared deterministic sampling
    ///     untouched, so nothing about that payload changes. Answer-variance mode advances the seed by the repeat index
    ///     off the same base seed and applies the requested temperature — the two runs of a group then differ in
    ///     exactly one input, which is what makes the spread of answers attributable.
    /// </summary>
    private static (int RepeatIndex, BenchmarkSamplingSnapshotV1 Sampling) SamplingFor(BenchmarkSamplingSnapshotV1 deterministic,
        BenchmarkRepeatMode mode,
        float temperature,
        int repeatIndex)
    {
        if (mode != BenchmarkRepeatMode.AnswerVariance)
        {
            return (repeatIndex, deterministic);
        }

        var baseSeed = SeedValue.TryParse(deterministic.SeedValue, out var parsed, out _) ? parsed ?? 0L : 0L;
        return (repeatIndex, deterministic with
        {
            Temperature = temperature,
            SeedValue = (baseSeed + repeatIndex).ToString(CultureInfo.InvariantCulture)
        });
    }

    /// <summary>
    ///     The temperature an answer-variance group samples at. Throughput mode never reads it, so a value carried on
    ///     a throughput request is ignored rather than refused — the two knobs are independent on the wire.
    /// </summary>
    private static float ResolveAnswerVarianceTemperature(BenchmarkRunStartRequest request)
    {
        if (request.RepeatMode != BenchmarkRepeatMode.AnswerVariance)
        {
            return 0f;
        }

        var temperature = request.AnswerVarianceTemperature ?? DefaultAnswerVarianceTemperature;
        if (temperature is <= 0f or > MaxAnswerVarianceTemperature)
        {
            throw new BenchmarkValidationException($"The answer-variance temperature must be above 0 and at most {MaxAnswerVarianceTemperature}.");
        }

        return temperature;
    }

    /// <summary>Warm-up is index 0 when requested; the measured repeats are always 1..N.</summary>
    private static IEnumerable<int> RepeatIndexes(int repeatCount, bool warmup) =>
        Enumerable.Range(warmup ? 0 : 1, repeatCount + (warmup ? 1 : 0));

    private static string GetApplicationVersion() =>
        typeof(BenchmarkRunFreezeService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BenchmarkRunFreezeService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private sealed class FreezeCommitGuard(
        IBenchmarkFreezeDependencyService dependencies,
        BenchmarkFreezeDependencySetV1 expected,
        Guid agentDefinitionId,
        ResolvedAgentRuntime runtime,
        string primaryModelName,
        string? judgeModelName) : IBenchmarkFreezeCommitGuard
    {
        public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
        {
            try
            {
                var current = await dependencies.CaptureAsync(agentDefinitionId, runtime, primaryModelName, judgeModelName, cancellationToken)
                                                .ConfigureAwait(false);
                return current == expected;
            }
            catch (BenchmarkEligibilityException)
            {
                return false;
            }
        }
    }
}
