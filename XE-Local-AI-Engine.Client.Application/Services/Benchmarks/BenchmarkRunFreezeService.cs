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
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

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
    double? AnswerVarianceTemperature = null);

/// <summary>
///     Work one launch REQUEST does once and every cell of it would otherwise repeat: the llama-server capability
///     probe, the variant it settles on, and the verified installed-model lease per model name. A batch of ten cells
///     used to run ten probes and ten full re-verifications of the same files, serially, before the endpoint answered.
///     <para>
///         Holding the leases for the request's lifetime is deliberate, not just cheaper: a model that changed halfway
///         through a matrix would give the later cells a different snapshot from the earlier ones, which is exactly the
///         variable a matrix exists to hold still. One probe for the same reason — asking twice can straddle a runtime
///         swap and freeze two different answers into one batch.
///     </para>
///     <para>
///         Not thread-safe: one scope belongs to one request, which processes its cells in order.
///     </para>
/// </summary>
public sealed class BenchmarkFreezeScope : IAsyncDisposable
{
    private readonly Dictionary<string, IBenchmarkInstalledModelLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private bool _inspected;
    private LlamaServerLaunchCapabilities? _capabilities;
    private GpuVariant? _variant;

    /// <summary>How many times the binary was actually inspected. Test-only seam.</summary>
    internal int Inspections { get; private set; }

    /// <summary>How many models were actually verified. Test-only seam.</summary>
    internal int Verifications { get; private set; }

    public async ValueTask DisposeAsync()
    {
        foreach (var lease in _leases.Values.Reverse())
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        _leases.Clear();
    }

    internal async Task<(LlamaServerLaunchCapabilities? Capabilities, GpuVariant Variant)> InspectAsync(IBenchmarkPhaseLaunchResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!_inspected)
        {
            _capabilities = await resolver.InspectAsync(cancellationToken).ConfigureAwait(false);
            _variant = await resolver.SelectVariantAsync(_capabilities, cancellationToken).ConfigureAwait(false);
            _inspected = true;
            Inspections++;
        }

        return (_capabilities, _variant!.Value);
    }

    internal async Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName,
        Func<string, CancellationToken, Task<IBenchmarkInstalledModelLease>> acquire,
        CancellationToken cancellationToken)
    {
        if (_leases.TryGetValue(modelName, out var cached))
        {
            return cached;
        }

        var lease = await acquire(modelName, cancellationToken).ConfigureAwait(false);
        _leases[modelName] = lease;
        Verifications++;
        return lease;
    }
}

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
    /// <param name="scope">
    ///     Work shared across one launch REQUEST — the capability probe and the verified model leases. Null makes the
    ///     call self-contained, which is what a single-run launch wants; a batch passes one scope through every cell so
    ///     the probe runs once and each distinct model is verified once. The caller owns the scope's lifetime.
    /// </param>
    Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(BenchmarkRunStartRequest request,
        BenchmarkFreezeScope? scope = null,
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
    ///     The most runs one freeze may enqueue, counting the product of LEAF task items and repeats (warm-up
    ///     included). <see cref="MaxRepeatCount" /> bounds one cell; this bounds the whole request, because a suite
    ///     multiplies the two and a matrix past this point is unschedulable rather than merely slow.
    /// </summary>
    public const int MaxRunsPerRequest = 100;

    /// <summary>
    ///     The temperature an answer-variance group samples at when the request pins none. 0.7 is the everyday chat
    ///     default: high enough that repeats actually diverge, low enough that the divergence is still the model
    ///     answering rather than wandering.
    /// </summary>
    public const double DefaultAnswerVarianceTemperature = 0.7d;

    /// <summary>The ceiling the chat sampling UI already enforces.</summary>
    public const double MaxAnswerVarianceTemperature = 2d;

    public async Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(BenchmarkRunStartRequest request,
        BenchmarkFreezeScope? scope = null,
        CancellationToken cancellationToken = default)
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

        // A scope of our own when the caller passed none, so the single-run path keeps behaving exactly as it did:
        // acquire, freeze, release. A caller-supplied scope outlives this call and is NOT disposed here.
        await using var ownedScope = scope is null ? new BenchmarkFreezeScope() : null;
        var freezeScope = scope ?? ownedScope!;
        var trimmedPrimary = primaryModelName.Trim();
        var primary = (await freezeScope.AcquireAsync(trimmedPrimary, AcquireVerifiedAsync, cancellationToken).ConfigureAwait(false)).Snapshot;
        BenchmarkModelEligibility.Validate(primary, "primary");

        // The judge is no longer part of the freeze: its runtime is resolved per attempt, against the policy
        // revision that attempt is judged under, so a judge change never re-freezes a run.
        var definition = await _agentDefinitions.GetByIdAsync(project.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.Kind != AgentDefinitionKind.Single)
        {
            throw new BenchmarkEligibilityException("The selected Single agent definition no longer exists.");
        }

        var capabilities = await _modelCapabilities.TryResolveAsync(primary.ModelName, cancellationToken).ConfigureAwait(false)
                           ?? throw new BenchmarkEligibilityException("The selected primary model capabilities are unavailable.");

        // What a freeze fans out over. A project created before task suites has no item rows until something
        // materializes item 0 from its core task; that lazy backfill is the only remaining reason a freeze writes.
        var items = await _benchmarkStore.ListTaskItemsAsync(project.Id, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            items = await _benchmarkStore.GetOrCreateItemsAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }

        // A generator is never a run target; the cases it expanded into are.
        var leafItems = items.Where(static item => item.IsLeaf).OrderBy(static item => item.Index).ToArray();
        if (leafItems.Length == 0)
        {
            throw new BenchmarkValidationException("The project has no runnable task item.");
        }

        // The second half of the long-context refusal. Expansion already compared these two numbers, but a project's
        // context window is editable afterwards, and a probe silently truncated to a smaller window measures the
        // window rather than the model — so the check that decides is the one taken against the context this freeze
        // is actually about to use.
        foreach (var probe in leafItems)
        {
            if (BenchmarkNiahCase.TryRead(probe) is { } probeCase && probeCase.ContextTokens > project.ContextTokens)
            {
                throw new BenchmarkValidationException(
                    $"The long-context probe '{probeCase.Label}' asks for {probeCase.ContextTokens} tokens, which does not fit the project's "
                    + $"{project.ContextTokens}-token context window.");
            }
        }

        var repeatIndexes = RepeatIndexes(repeatCount, warmup).ToArray();
        var runCount = leafItems.Length * repeatIndexes.Length;
        if (runCount > MaxRunsPerRequest)
        {
            throw new BenchmarkValidationException(
                $"This request would start {runCount} runs ({leafItems.Length} task items x {repeatIndexes.Length} runs each). "
                + $"The maximum is {MaxRunsPerRequest}.");
        }

        var primarySnapshot = BenchmarkInstalledModelSnapshotMapper.ToSnapshot(primary);

        // One capability read per REQUEST: the primary and the judge launch the same binary, asking twice could
        // straddle a runtime swap and freeze two different answers into one batch, and the variant is taken from
        // the same inspection that produced the capabilities so a second selection cannot disagree with the
        // manifest whose digest we record.
        var (binaryCapabilities, variant) = await freezeScope.InspectAsync(_launchResolver, cancellationToken).ConfigureAwait(false);
        var primaryLaunch = await _launchResolver
                                  .ResolveAsync(primary.ModelName, project.ContextTokens, requestedKvCacheType, binaryCapabilities, variant, cancellationToken)
                                  .ConfigureAwait(false);
        // The enforceability answer is frozen off the capabilities read ABOVE, not re-resolved at execution: a
        // model swap or a re-detection between freeze and run must not change what a frozen run replays.
        //
        // SupportsThinking is half the answer, not a separate question. A model that does not reason at all cannot
        // have its reasoning capped, and GgufModelCapabilities defaults ReasoningBudgetEnforceable to true — the inert
        // safe answer for a model nothing was detected about — so freezing that field alone said "enforceable" for
        // every non-thinking model. The budget then went out on the wire, llama-server accepted it and ignored it
        // (no think-end tags in the template), and the one thing that would have told the operator — the
        // ReasoningBudgetSkipLog notice — never fired, because the marker was written rather than skipped.
        var reasoningBudgetEnforceable = capabilities.SupportsThinking && capabilities.ReasoningBudgetEnforceable;
        var primarySampling = BenchmarkFrozenPolicies.DeterministicSampling(project.MaxOutputTokens,
            project.ReasoningBudgetTokens,
            project.ReasoningBudgetTokens is null ? null : reasoningBudgetEnforceable);
        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // ONE resolution per LEAF ITEM. The task text is the resolver's retrieval query, so the system prompt and the
        // skills behind it can legitimately differ between items, and the dependency set that guards the commit is
        // derived from that resolution. The binary capability probe and the variant selection above stay ONCE per
        // freeze — a second selection could disagree with the manifest whose digest we record.
        var frozenItems = new List<FrozenTaskItem>(leafItems.Length);
        foreach (var item in leafItems)
        {
            var itemCoreTask = BenchmarkTaskItemService.DecodePrompt(item.PromptJson.Span);
            var resolved = await _agentResolver.ResolveAsync(project.AgentDefinitionId,
                                                   primaryModelName,
                                                   itemCoreTask,
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
            frozenItems.Add(new FrozenTaskItem(item,
                itemCoreTask,
                eligible,
                new FreezeCommitGuard(_dependencies, dependencySet, project.AgentDefinitionId, eligible, primaryModelName, judgeModelName: null),
                dependencySet));
        }

        // One snapshot per DISTINCT (item, sampling), memoized. Throughput mode has exactly one sampling, so a
        // single-item project's group is byte-for-byte the single shared payload it has always been; answer-variance
        // mode gets one per repeat, differing ONLY in the seed. The ITEM half of the key is load-bearing: P1 keyed
        // this cache on the seed alone, and fanning out over items without widening it would hand every item the
        // FIRST item's serialized snapshot — every run answering item 0's prompt while its task_item_id column
        // claimed otherwise, with nothing failing loudly.
        var serializedSnapshots = new Dictionary<(Guid ItemId, string Seed), byte[]>();

        byte[] SnapshotFor(FrozenTaskItem item, BenchmarkSamplingSnapshotV1 sampling)
        {
            var key = (item.Item.Id, sampling.SeedValue ?? string.Empty);
            if (serializedSnapshots.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var created = _snapshots.Serialize(_snapshots.Create(new BenchmarkRuntimeSnapshotInput(project.Id,
                definition.Id,
                definition.Version,
                item.CoreTask,
                project.ContextTokens,
                item.Eligible,
                primaryLaunch.Runtime,
                sampling,
                primarySnapshot,
                item.Dependencies,
                GetApplicationVersion(),
                createdAtUtc)));
            serializedSnapshots[key] = created;
            return created;
        }

        // A repeat group only exists when there is something to group: a plain single run keeps NULL in all three
        // columns so nothing about the old shape changes for it.
        var isGroup = repeatCount > 1 || warmup;
        var repeatGroupId = isGroup ? Guid.NewGuid() : (Guid?)null;

        // A CELL groups the ITEMS of one measurement; a REPEAT GROUP groups the REPEATS of one item. They coincide
        // whenever both exist — same GUID, one identity, nothing to keep in sync — and a multi-item freeze needs a
        // cell even when it has no repeats to group. Deriving the cell from the repeat group alone put every run of a
        // 3-item single-repeat suite in its own singleton cell, so every cell was missing two of three items and the
        // project ranked nothing.
        var cellGroupId = repeatGroupId ?? (leafItems.Length > 1 ? Guid.NewGuid() : (Guid?)null);

        // Null lets the store stamp the run's own singleton cell, which is what a one-item one-repeat freeze is and
        // what every pre-suite run already carries. A warm-up sits at index 0 and so forms its own cell, which the
        // ranking read drops before grouping — a stamp is an identity, not a ranking decision.
        string? CellKeyFor(int repeatIndex) =>
            cellGroupId is { } id
                ? "cell:" + id.ToString("D") + ":" + repeatIndex.ToString(CultureInfo.InvariantCulture)
                : null;

        // The work queue is FIFO by queue sequence, so building the commands in this order is what makes the
        // repeats run back-to-back — warm-up first, then 1..N — rather than interleaved with whatever else is
        // queued. Items are the INNER loop, so a partially drained queue yields whole comparable cells rather than
        // one item across every cell. The whole group goes in through ONE store call: a per-run insert, each chaining
        // its compare-and-swap on its predecessor, let a concurrent writer land mid-group, so the caller got a
        // conflict and no ids while the runs already inserted stayed queued and ran anyway.
        var commands = repeatIndexes
                       .Select(repeatIndex => SamplingFor(primarySampling, request.RepeatMode, temperature, repeatIndex))
                       .SelectMany(sampling => frozenItems.Select(item => new BenchmarkStartRunCommand(Guid.NewGuid(),
                           project.Id,
                           expectedProjectVersion,
                           SnapshotFor(item, sampling.Sampling),
                           primary.ModelName,
                           primary.Origin,
                           primary.ModelContentFingerprint,
                           item.Eligible.AgentName,
                           item.Eligible.AgentDefinitionVersion,
                           project.ContextTokens,
                           item.Guard,
                           primaryLaunch.Intent,
                           repeatGroupId,
                           isGroup ? sampling.RepeatIndex : null,
                           warmup && sampling.RepeatIndex == 0,
                           // Copied onto the run, not read from the project at execution: a run replays with the
                           // budget it was started under, exactly like its context and its output budget.
                           project.InvocationTimeoutSeconds,
                           request.RepeatMode,
                           sampling.Sampling.SeedValue,
                           sampling.Sampling.Temperature,
                           item.Item.Id,
                           item.Item.Index,
                           CellKeyFor(isGroup ? sampling.RepeatIndex : 1),
                           item.Item.InputHash,
                           project.TaskItemSetHash)))
                       .ToArray();
        var runs = await _benchmarkStore.StartRunsAsync(commands, expectedProjectVersion, cancellationToken).ConfigureAwait(false);

        _queueSignal?.Wake();
        return runs;
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
        double temperature,
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
    private static double ResolveAnswerVarianceTemperature(BenchmarkRunStartRequest request)
    {
        if (request.RepeatMode != BenchmarkRepeatMode.AnswerVariance)
        {
            return 0d;
        }

        var temperature = request.AnswerVarianceTemperature ?? DefaultAnswerVarianceTemperature;
        if (temperature is <= 0d or > MaxAnswerVarianceTemperature)
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

    /// <summary>
    ///     One leaf task item, resolved: the prompt a run of it is asked, the agent runtime that prompt resolved to,
    ///     the dependency set captured from that resolution, and the guard that re-checks the set at commit. One per
    ///     item, because the task text is the resolver's retrieval query.
    /// </summary>
    private sealed record FrozenTaskItem(BenchmarkTaskItemRecord Item,
        string CoreTask,
        ResolvedAgentRuntime Eligible,
        FreezeCommitGuard Guard,
        BenchmarkFreezeDependencySetV1 Dependencies);

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
