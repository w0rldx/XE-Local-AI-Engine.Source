namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Reflection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;

public interface IBenchmarkRunFreezeService
{
    /// <param name="kvCacheType">
    ///     The KV-cache type the run asked for, or <see langword="null" /> for Auto (freeze picks). Must already be
    ///     canonical — see <see cref="BenchmarkKvCacheType.TryNormalize" />.
    /// </param>
    /// <param name="repeatCount">
    ///     How many measured runs to enqueue, 1..<see cref="BenchmarkRunFreezeService.MaxRepeatCount" />. Everything is
    ///     frozen ONCE and the repeats share that snapshot, so they differ only in what the machine did.
    /// </param>
    /// <param name="warmup">
    ///     Prepends one more run at repeat index 0, flagged <c>IsWarmup</c>: never ranked, never counted in a group's
    ///     statistics. It exists to absorb the first-launch costs (page cache cold, GPU clocks low) the measured
    ///     repeats should not pay.
    /// </param>
    /// <returns>
    ///     The created runs in queue order — the warm-up first when one was asked for, then repeats 1..N. Never empty.
    /// </returns>
    /// <remarks>
    ///     A repeat is a fresh <c>llama-server</c> per run, by the unchanged design of the benchmark queue: each run
    ///     claims the exclusive runtime, spawns, measures, and releases it. So repeats measure cold-launch to
    ///     cold-launch variance INCLUDING model load, not steady-state variance within one process. That is deliberate
    ///     and is what an operator comparing two models on this node actually experiences. And because the frozen
    ///     sampling is deterministic (temperature 0, fixed seed), the ANSWER is the same across repeats — what repeats
    ///     quantify is throughput jitter, not answer variance.
    /// </remarks>
    Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(Guid projectId,
        string primaryModelName,
        long expectedProjectVersion,
        string? kvCacheType = null,
        int repeatCount = 1,
        bool warmup = false,
        CancellationToken cancellationToken = default);
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
    IBenchmarkQueueSignal? queueSignal = null) : IBenchmarkRunFreezeService
{
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

    public async Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(Guid projectId,
        string primaryModelName,
        long expectedProjectVersion,
        string? kvCacheType = null,
        int repeatCount = 1,
        bool warmup = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryModelName);
        if (repeatCount is < 1 or > MaxRepeatCount)
        {
            throw new BenchmarkValidationException($"Repeat count must be between 1 and {MaxRepeatCount}.");
        }

        if (!BenchmarkKvCacheType.TryNormalize(kvCacheType, out var requestedKvCacheType))
        {
            throw new BenchmarkValidationException("The requested KV-cache type is not supported.");
        }

        var project = await _benchmarkStore.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
                      ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");
        if (project.Version != expectedProjectVersion)
        {
            throw new BenchmarkConflictException("VersionConflict");
        }

        var leases = new Dictionary<string, IBenchmarkInstalledModelLease>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var trimmedPrimary = primaryModelName.Trim();
            leases.Add(trimmedPrimary, await _installedModels.AcquireAsync(trimmedPrimary, cancellationToken).ConfigureAwait(false));
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
            var primarySampling = BenchmarkFrozenPolicies.DeterministicSampling(project.MaxOutputTokens);
            var snapshot = _snapshots.Create(new BenchmarkRuntimeSnapshotInput(project.Id,
                definition.Id,
                definition.Version,
                exactCoreTask,
                project.ContextTokens,
                eligible,
                primaryLaunch.Runtime,
                primarySampling,
                primarySnapshot,
                dependencySet,
                GetApplicationVersion(),
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
            var serializedSnapshot = _snapshots.Serialize(snapshot);
            var guard = new FreezeCommitGuard(_dependencies, dependencySet, project.AgentDefinitionId, eligible, primaryModelName,
                judgeModelName: null);

            // A group only exists when there is something to group: a plain single run keeps NULL in all three columns
            // so nothing about the old shape changes for it.
            var isGroup = repeatCount > 1 || warmup;
            var repeatGroupId = isGroup ? Guid.NewGuid() : (Guid?)null;

            // The work queue is FIFO by queue sequence, so inserting in this order is what makes the repeats run
            // back-to-back — warm-up first, then 1..N — rather than interleaved with whatever else is queued.
            // Each insert bumps the project version by exactly one, so the expected version for insert i is
            // `expectedProjectVersion + i`: the CAS of the FIRST insert is the caller's, and every later one is this
            // method's own predecessor. A conflict therefore still surfaces on the very first insert, before anything
            // was created.
            var runs = new List<BenchmarkRunRecord>(repeatCount + (warmup ? 1 : 0));
            foreach (var repeatIndex in RepeatIndexes(repeatCount, warmup))
            {
                var command = new BenchmarkStartRunCommand(Guid.NewGuid(),
                    project.Id,
                    expectedProjectVersion + runs.Count,
                    serializedSnapshot,
                    primary.ModelName,
                    primary.Origin,
                    primary.ModelContentFingerprint,
                    eligible.AgentName,
                    eligible.AgentDefinitionVersion,
                    project.ContextTokens,
                    guard,
                    primaryLaunch.Intent,
                    repeatGroupId,
                    isGroup ? repeatIndex : null,
                    warmup && repeatIndex == 0,
                    // Copied onto the run, not read from the project at execution: a run replays with the budget it was
                    // started under, exactly like its context and its output budget.
                    project.InvocationTimeoutSeconds);
                runs.Add(await _benchmarkStore.StartRunAsync(command, cancellationToken).ConfigureAwait(false));
            }

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
