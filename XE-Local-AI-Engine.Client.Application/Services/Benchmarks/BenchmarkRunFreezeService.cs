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
    Task<BenchmarkRunRecord> StartAsync(Guid projectId,
        string primaryModelName,
        long expectedProjectVersion,
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
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IBenchmarkQueueSignal? _queueSignal = queueSignal;

    public async Task<BenchmarkRunRecord> StartAsync(Guid projectId,
        string primaryModelName,
        long expectedProjectVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryModelName);
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
            var primaryRuntime = await ResolveRuntimeAsync(primary.ModelName, project.ContextTokens, cancellationToken).ConfigureAwait(false);
            var primarySampling = BenchmarkFrozenPolicies.DeterministicSampling();
            var judgeSnapshot = await CreateJudgeSnapshotAsync(project, judge, cancellationToken).ConfigureAwait(false);
            var snapshot = _snapshots.Create(new BenchmarkRuntimeSnapshotInput(project.Id,
                definition.Id,
                definition.Version,
                exactCoreTask,
                project.ContextTokens,
                eligible,
                primaryRuntime,
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
                new FreezeCommitGuard(_dependencies, dependencySet, project.AgentDefinitionId, eligible, primaryModelName, judge?.ModelName));
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

    private async Task<BenchmarkJudgeSnapshotV1> CreateJudgeSnapshotAsync(BenchmarkProjectRecord project,
        InstalledModelSnapshot? judge,
        CancellationToken cancellationToken)
    {
        if (!BenchmarkFrozenPolicies.SupportsVersions(project.JudgePromptVersion, project.JudgeOutputSchemaVersion))
        {
            throw new BenchmarkEligibilityException("The benchmark judge prompt or output schema version is not supported.");
        }

        if (!project.JudgeEnabled)
        {
            return new BenchmarkJudgeSnapshotV1(false, null, project.JudgePromptVersion, project.JudgeOutputSchemaVersion, null,
                null, null, null, null,
                Hash(new
                {
                    project.JudgePromptVersion,
                    project.JudgeOutputSchemaVersion,
                    Enabled = false
                }));
        }

        var model = ToSnapshot(judge ?? throw new BenchmarkEligibilityException("The selected judge model is not installed."));
        var contextTokens = project.JudgeContextTokens!.Value;
        var runtime = await ResolveRuntimeAsync(model.ModelName, contextTokens, cancellationToken).ConfigureAwait(false);
        var sampling = BenchmarkFrozenPolicies.DeterministicSampling();
        return new BenchmarkJudgeSnapshotV1(true,
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
            }));
    }

    private async Task<BenchmarkLlamaRuntimeSnapshotV1> ResolveRuntimeAsync(string modelName,
        int requiredContextTokens,
        CancellationToken cancellationToken)
    {
        var variant = await _variantSelector.SelectVariantAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await _inferenceProfiles.ResolveAsync(modelName, ModelRole.Chat, variant, cancellationToken).ConfigureAwait(false);
        if (resolved.ExploreMode)
        {
            resolved = ResolvedLaunchArguments.Replay(requiredContextTokens);
        }

        if (resolved.CtxSize < requiredContextTokens)
        {
            throw new BenchmarkEligibilityException("The resolved llama.cpp runtime context is smaller than the benchmark requirement.");
        }

        return new BenchmarkLlamaRuntimeSnapshotV1(variant,
            resolved.CtxSize,
            resolved.NGpuLayers,
            resolved.TensorSplit,
            resolved.OverrideTensor,
            resolved.KvTypeK,
            resolved.KvTypeV,
            resolved.FlashAttn,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);
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
}
