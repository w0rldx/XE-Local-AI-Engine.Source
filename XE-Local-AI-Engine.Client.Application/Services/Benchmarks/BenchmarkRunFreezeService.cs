namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Reflection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public interface IBenchmarkRunFreezeService
{
    /// <param name="kvCacheType">
    ///     The KV-cache type the run asked for, or <see langword="null" /> for Auto (freeze picks). Must already be
    ///     canonical — see <see cref="BenchmarkKvCacheType.TryNormalize" />.
    /// </param>
    Task<BenchmarkRunRecord> StartAsync(Guid projectId,
        string primaryModelName,
        long expectedProjectVersion,
        string? kvCacheType = null,
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

    public async Task<BenchmarkRunRecord> StartAsync(Guid projectId,
        string primaryModelName,
        long expectedProjectVersion,
        string? kvCacheType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryModelName);
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
            var primarySampling = BenchmarkFrozenPolicies.DeterministicSampling();
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
            var command = new BenchmarkStartRunCommand(Guid.NewGuid(),
                project.Id,
                expectedProjectVersion,
                _snapshots.Serialize(snapshot),
                primary.ModelName,
                primary.Origin,
                primary.ModelContentFingerprint,
                eligible.AgentName,
                eligible.AgentDefinitionVersion,
                project.ContextTokens,
                new FreezeCommitGuard(_dependencies, dependencySet, project.AgentDefinitionId, eligible, primaryModelName, judgeModelName: null),
                primaryLaunch.Intent);
            var run = await _benchmarkStore.StartRunAsync(command, cancellationToken).ConfigureAwait(false);
            _queueSignal?.Wake();
            return run;
        }
        finally
        {
            foreach (var lease in leases.Values.Reverse())
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

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

internal static class BenchmarkModelEligibility
{
    /// <summary>
    ///     Admits local llama.cpp chat GGUFs only. An attached <c>mmproj</c> projector member is NOT disqualifying:
    ///     the HF acquisition path auto-attaches one to modern text models (gemma-4, Qwen3.x), and it is an optional
    ///     companion the chat runtime passes as <c>--mmproj</c> without changing text generation. The benchmark itself
    ///     stays text-only — it never sends image content — so a projector-bearing chat model measures the same as a
    ///     bare one. Genuine vision/projector-only models are excluded by their <see cref="GgufRole" />, not by this.
    /// </summary>
    public static void Validate(InstalledModelSnapshot snapshot, string role)
    {
        if (!string.Equals(snapshot.ProviderName, "llamacpp", StringComparison.OrdinalIgnoreCase)
            || snapshot.Role != GgufRole.Chat)
        {
            throw new BenchmarkEligibilityException($"The selected {role} model is not an eligible local text-generation GGUF.");
        }
    }

    /// <summary>
    ///     The judge is held to a stricter rule than the primary: a model carrying an auxiliary asset (a projector, and
    ///     by extension any adapter or draft companion) launches with <c>--mmproj</c>/<c>--lora</c>/<c>-md</c>, and the
    ///     launch receipt records only THAT something extra was loaded, never which file. Such a judging can never be
    ///     shown to be the same execution as another, so it could never join a rank cohort — refuse it at the policy
    ///     instead of letting every run it scores come out permanently unranked.
    /// </summary>
    public static void ValidateJudge(InstalledModelSnapshot snapshot)
    {
        Validate(snapshot, "judge");
        if (snapshot.Members.Any(static member => member.Role == InstalledModelPhysicalMemberRole.Projector))
        {
            throw new BenchmarkEligibilityException(
                "The selected judge model carries an auxiliary asset (projector, adapter or draft model). Judgings from such a model cannot be ranked; pick a plain text GGUF.");
        }
    }
}
