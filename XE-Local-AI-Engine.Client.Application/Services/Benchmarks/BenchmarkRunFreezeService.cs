namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
    IInferenceProfileResolver inferenceProfiles,
    IGpuVariantSelector variantSelector,
    ILlamaServerLaunchCapabilityInspector launchCapabilities,
    ILlamaServerLaunchFallbackStore launchFallbackStore,
    ILlamaServerLaunchPolicy launchPolicy,
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
    private readonly IInferenceProfileResolver _inferenceProfiles = inferenceProfiles ?? throw new ArgumentNullException(nameof(inferenceProfiles));
    private readonly IGpuVariantSelector _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));

    private readonly ILlamaServerLaunchCapabilityInspector _launchCapabilities =
        launchCapabilities ?? throw new ArgumentNullException(nameof(launchCapabilities));

    private readonly ILlamaServerLaunchFallbackStore _launchFallbackStore =
        launchFallbackStore ?? throw new ArgumentNullException(nameof(launchFallbackStore));

    private readonly ILlamaServerLaunchPolicy _launchPolicy = launchPolicy ?? throw new ArgumentNullException(nameof(launchPolicy));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IBenchmarkQueueSignal? _queueSignal = queueSignal;

    /// <summary>Auto stayed on f16 because the node selected a CPU llama.cpp build.</summary>
    public const string AutoReasonCpuVariant = "cpu-variant";

    /// <summary>Auto stayed on f16 because the selected binary could not be interrogated.</summary>
    public const string AutoReasonProbeUnavailable = "probe-unavailable";

    /// <summary>Auto stayed on f16 because the selected binary does not advertise the quantized vector.</summary>
    public const string AutoReasonManifestUnsupported = "manifest-unsupported";

    /// <summary>Auto stayed on f16 because the optimized config was previously recorded as unable to start here.</summary>
    public const string AutoReasonFallbackDisabled = "fallback-disabled";

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

        var requestedModels = new[]
                              {
                                  primaryModelName.Trim(),
                                  project.JudgeEnabled ? project.JudgeModelName : null
                              }
                              .Where(static name => !string.IsNullOrWhiteSpace(name))
                              .Select(static name => name!)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(static name => name, StringComparer.Ordinal)
                              .ToArray();
        var leases = new Dictionary<string, IBenchmarkInstalledModelLease>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var modelName in requestedModels)
            {
                leases.Add(modelName, await _installedModels.AcquireAsync(modelName, cancellationToken).ConfigureAwait(false));
            }

            var primary = leases[primaryModelName.Trim()].Snapshot;
            BenchmarkModelEligibility.Validate(primary, "primary");
            var judge = project.JudgeEnabled ? leases[project.JudgeModelName!].Snapshot : null;
            if (judge is not null)
            {
                BenchmarkModelEligibility.Validate(judge, "judge");
            }

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
                                                       judge?.ModelName,
                                                       cancellationToken)
                                                   .ConfigureAwait(false);
            var primarySnapshot = ToSnapshot(primary);

            // One capability read per freeze: the primary and the judge launch the same binary, and asking twice could
            // straddle a runtime swap and freeze two different answers into one run.
            var binaryCapabilities = await InspectLaunchCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

            // ONE variant for the whole freeze, taken from the inspection that produced the capabilities: both phases
            // launch the same binary, and a second selection could disagree with the manifest whose digest we record.
            var variant = binaryCapabilities?.Variant ?? await _variantSelector.SelectVariantAsync(cancellationToken).ConfigureAwait(false);
            var primaryLaunch = await ResolveRuntimeAsync(primary.ModelName, project.ContextTokens, requestedKvCacheType, binaryCapabilities, variant, cancellationToken)
                .ConfigureAwait(false);
            var primarySampling = BenchmarkFrozenPolicies.DeterministicSampling();
            var (judgeSnapshot, judgeLaunch) = await CreateJudgeSnapshotAsync(project, judge, binaryCapabilities, variant, cancellationToken).ConfigureAwait(false);
            var snapshot = _snapshots.Create(new BenchmarkRuntimeSnapshotInput(project.Id,
                definition.Id,
                definition.Version,
                exactCoreTask,
                project.ContextTokens,
                eligible,
                primaryLaunch.Runtime,
                primarySampling,
                primarySnapshot,
                judgeSnapshot,
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
                project.JudgeEnabled,
                new FreezeCommitGuard(_dependencies, dependencySet, project.AgentDefinitionId, eligible, primaryModelName, judge?.ModelName),
                primaryLaunch.Intent,
                judgeLaunch?.Intent);
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

    private async Task<FrozenJudge> CreateJudgeSnapshotAsync(BenchmarkProjectRecord project,
        InstalledModelSnapshot? judge,
        LlamaServerLaunchCapabilities? capabilities,
        GpuVariant variant,
        CancellationToken cancellationToken)
    {
        if (!BenchmarkFrozenPolicies.SupportsVersions(project.JudgePromptVersion, project.JudgeOutputSchemaVersion))
        {
            throw new BenchmarkEligibilityException("The benchmark judge prompt or output schema version is not supported.");
        }

        if (!project.JudgeEnabled)
        {
            return new FrozenJudge(new BenchmarkJudgeSnapshotV1(false, null, project.JudgePromptVersion, project.JudgeOutputSchemaVersion, null,
                null, null, null, null,
                Hash(new
                {
                    project.JudgePromptVersion,
                    project.JudgeOutputSchemaVersion,
                    Enabled = false
                })),
                Launch: null);
        }

        var model = ToSnapshot(judge ?? throw new BenchmarkEligibilityException("The selected judge model is not installed."));
        var contextTokens = project.JudgeContextTokens!.Value;

        // The judge is scoring, not being measured: it never takes the run's KV pick, only Auto.
        var launch = await ResolveRuntimeAsync(model.ModelName, contextTokens, requestedKvCacheType: null, capabilities, variant, cancellationToken).ConfigureAwait(false);
        var runtime = launch.Runtime;
        var sampling = BenchmarkFrozenPolicies.DeterministicSampling();
        return new FrozenJudge(new BenchmarkJudgeSnapshotV1(true,
            model,
            project.JudgePromptVersion,
            project.JudgeOutputSchemaVersion,
            contextTokens,
            BenchmarkFrozenPolicies.JudgeSystemPrompt,
            BenchmarkFrozenPolicies.JudgeOutputSchemaJson,
            runtime,
            sampling,
            Hash(new
            {
                project.JudgePromptVersion,
                project.JudgeOutputSchemaVersion,
                ContextTokens = contextTokens,
                model.ModelContentFingerprint,
                Runtime = runtime,
                Sampling = sampling,
                BenchmarkFrozenPolicies.JudgeSystemPrompt,
                BenchmarkFrozenPolicies.JudgeOutputSchemaJson
            })),
            launch);
    }

    /// <summary>
    ///     Freezes one phase's launch vector: the profile replay, the KV-cache type it will run with, and the launch
    ///     identity that vector is INTENDED to produce. The frozen placement (<c>-c/-ngl/-ts/-ot</c>) is carried
    ///     through untouched, so a KV pick never re-fits the run.
    /// </summary>
    private async Task<BenchmarkFrozenLaunch> ResolveRuntimeAsync(string modelName,
        int requiredContextTokens,
        string? requestedKvCacheType,
        LlamaServerLaunchCapabilities? capabilities,
        GpuVariant variant,
        CancellationToken cancellationToken)
    {
        var resolved = await _inferenceProfiles.ResolveAsync(modelName, ModelRole.Chat, variant, cancellationToken).ConfigureAwait(false);
        if (resolved.ExploreMode)
        {
            resolved = ResolvedLaunchArguments.Replay(requiredContextTokens);
        }

        if (resolved.CtxSize < requiredContextTokens)
        {
            throw new BenchmarkEligibilityException("The resolved llama.cpp runtime context is smaller than the benchmark requirement.");
        }

        // Auto is the only decision the fallback store participates in: an explicit pick is answered from the
        // manifest alone, so an operator can always retry a config a previous host state disabled.
        var optimizedDisabled = requestedKvCacheType is null
                                && variant != GpuVariant.Cpu
                                && await _launchFallbackStore.IsOptimizedConfigDisabledAsync(variant, cancellationToken).ConfigureAwait(false);
        var (effective, source, reason) = ResolveKvCacheType(requestedKvCacheType, variant, capabilities, optimizedDisabled);
        var applied = BenchmarkKvCacheType.Apply(resolved, effective);

        // The plan the supervisor will build for this spawn: a benchmark launch applies no launch policy, so a GPU
        // replay gets a null plan and a CPU replay the two args a CPU build can honour.
        var plan = variant == GpuVariant.Cpu ? _launchPolicy.ResolveCpuReplayPlan(applied) : (LlamaServerLaunchPlan?)null;
        var policy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1;
        var intendedIdentity = LlamaServerLaunchProjection.From(variant, applied, plan, ModelRole.Chat, policy.ChatCacheReuse, policy.ChatCacheRamMiB)
                                                          .ComputeIdentity();
        return new BenchmarkFrozenLaunch(new BenchmarkLlamaRuntimeSnapshotV1(variant,
                applied.CtxSize,
                applied.NGpuLayers,
                applied.TensorSplit,
                applied.OverrideTensor,
                applied.KvTypeK,
                applied.KvTypeV,
                applied.FlashAttn,
                policy),
            new BenchmarkRunLaunchIntent(BenchmarkLaunchBackend.VariantName(variant),
                effective,
                source,
                reason,
                BenchmarkKvCacheType.IsQuantized(effective) ? LlamaServerLaunchProjection.FlashAttentionOn : LlamaServerLaunchProjection.FlashAttentionAuto,
                intendedIdentity,
                capabilities?.ManifestSha256));
    }

    /// <summary>
    ///     The KV-cache type this launch will actually use. Auto degrades to <c>f16</c> with a recorded reason; an
    ///     explicit quantized pick the selected binary cannot be shown to accept is refused (422) rather than
    ///     discovered as a failed spawn.
    /// </summary>
    private static KvCacheResolution ResolveKvCacheType(string? requested,
        GpuVariant variant,
        LlamaServerLaunchCapabilities? capabilities,
        bool optimizedDisabled)
    {
        var probed = capabilities is { ProbeSucceeded: true };
        var isGpu = variant != GpuVariant.Cpu;
        if (requested is null)
        {
            if (!isGpu)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonCpuVariant);
            }

            if (!probed)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonProbeUnavailable);
            }

            if (optimizedDisabled)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonFallbackDisabled);
            }

            return Accepts(capabilities!, BenchmarkKvCacheType.Q8_0)
                ? new KvCacheResolution(BenchmarkKvCacheType.Q8_0, BenchmarkKvCacheType.SourceAuto, null)
                : new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonManifestUnsupported);
        }

        if (!BenchmarkKvCacheType.IsQuantized(requested))
        {
            return new KvCacheResolution(requested, BenchmarkKvCacheType.SourceExplicit, null);
        }

        if (!isGpu)
        {
            throw new BenchmarkUnsupportedKvCacheTypeException(
                $"A {requested} KV cache needs a GPU llama.cpp build, and this node selected the CPU build. Pick f16.");
        }

        if (!probed)
        {
            throw new BenchmarkUnsupportedKvCacheTypeException(
                $"The selected llama.cpp binary could not be inspected, so a {requested} KV cache cannot be confirmed. Pick f16 or repair the llama.cpp runtime.");
        }

        if (!Accepts(capabilities!, requested))
        {
            throw new BenchmarkUnsupportedKvCacheTypeException(
                $"The selected llama.cpp binary does not accept a {requested} KV cache with flash attention. Pick f16.");
        }

        return new KvCacheResolution(requested, BenchmarkKvCacheType.SourceExplicit, null);
    }

    private static bool Accepts(LlamaServerLaunchCapabilities capabilities, string cacheType) =>
        capabilities.SupportsCacheTypeK(cacheType)
        && capabilities.SupportsCacheTypeV(cacheType)
        && capabilities.SupportsFlashAttentionMode(LlamaServerLaunchProjection.FlashAttentionOn);

    /// <summary>
    ///     Reads what the llama-server binary this node would launch accepts. A binary that cannot be acquired is
    ///     recorded as "not inspected" rather than failing the freeze: the run is still frozen truthfully, Auto stays
    ///     on f16, and the spawn reports the real acquisition failure.
    /// </summary>
    private async Task<LlamaServerLaunchCapabilities?> InspectLaunchCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _launchCapabilities.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LlamaRuntimeException)
        {
            return null;
        }
    }

    private static BenchmarkInstalledModelSnapshotV1 ToSnapshot(InstalledModelSnapshot source) =>
        new(source.ModelName,
            source.RegistryRevision,
            source.RegistryAliases.Select(static alias => new BenchmarkRegistryAliasSnapshotV1(alias.ModelName, alias.RegistryRevision)).ToArray(),
            source.RegistryAliasSetHash,
            source.Members.Select(static member => new BenchmarkPhysicalMemberSnapshotV1(member.RelativePath,
                      member.Role,
                      member.SizeBytes,
                      member.Sha256,
                      member.OwningAliases.ToArray(),
                      member.Required,
                      member.MetadataSchemaVersion,
                      member.MemberFingerprint))
                  .ToArray(),
            source.PhysicalMemberSetHash,
            source.Origin,
            source.ProviderName!,
            source.ProviderMappingRevision,
            source.RepoId,
            source.SourceRevision,
            Path.GetFileName(source.Members.First(static member => member.Role == InstalledModelPhysicalMemberRole.Weight).RelativePath),
            source.Quantization,
            source.Role switch
            {
                GgufRole.Chat => "chat",
                GgufRole.Embedding => "embedding",
                GgufRole.Draft => "draft",
                _ => "unknown"
            },
            source.ModelContentFingerprint);

    private static string GetApplicationVersion() =>
        typeof(BenchmarkRunFreezeService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BenchmarkRunFreezeService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string Hash<T>(T value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)))}";

    private sealed record BenchmarkFrozenLaunch(BenchmarkLlamaRuntimeSnapshotV1 Runtime, BenchmarkRunLaunchIntent Intent);

    // The frozen judge turn: its snapshot, plus the launch it needs when the judge is enabled (null when it is not, in
    // which case no judge runtime is reserved).
    private sealed record FrozenJudge(BenchmarkJudgeSnapshotV1 Snapshot, BenchmarkFrozenLaunch? Launch);

    // The KV-cache type a launch will actually use: the effective type, whether it was picked explicitly or resolved by
    // Auto, and — for Auto — the reason it degraded (null when it did not).
    private sealed record KvCacheResolution(string Effective, string Source, string? Reason);

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
