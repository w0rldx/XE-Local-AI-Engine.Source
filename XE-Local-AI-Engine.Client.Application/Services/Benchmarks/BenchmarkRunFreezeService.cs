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
    IModelCapabilityResolver modelCapabilities,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    IBenchmarkEligibilityPolicy eligibilityPolicy,
    IBenchmarkFreezeDependencyService dependencies,
    IBenchmarkRuntimeSnapshotFactory snapshots,
    TimeProvider timeProvider,
    IBenchmarkQueueSignal? queueSignal = null) : IBenchmarkRunFreezeService
{
    private readonly IBenchmarkStore _benchmarkStore = benchmarkStore ?? throw new ArgumentNullException(nameof(benchmarkStore));
    private readonly IAgentDefinitionStore _agentDefinitions = agentDefinitions ?? throw new ArgumentNullException(nameof(agentDefinitions));
    private readonly IAgentDefinitionResolver _agentResolver = agentResolver ?? throw new ArgumentNullException(nameof(agentResolver));
    private readonly IModelCapabilityResolver _modelCapabilities = modelCapabilities ?? throw new ArgumentNullException(nameof(modelCapabilities));
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels = installedModels ?? throw new ArgumentNullException(nameof(installedModels));
    private readonly IBenchmarkEligibilityPolicy _eligibilityPolicy = eligibilityPolicy ?? throw new ArgumentNullException(nameof(eligibilityPolicy));
    private readonly IBenchmarkFreezeDependencyService _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IBenchmarkRuntimeSnapshotFactory _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
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

        var requestedModels = new[] { primaryModelName.Trim(), project.JudgeEnabled ? project.JudgeModelName : null }
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
            var (_, supportsTools, isCloud) = await _modelCapabilities.ResolveAsync(primaryModelName, cancellationToken).ConfigureAwait(false);
            if (isCloud)
            {
                throw new BenchmarkEligibilityException("Benchmark primary models must be local.");
            }

            var resolved = await _agentResolver.ResolveAsync(project.AgentDefinitionId,
                               primaryModelName,
                               exactCoreTask,
                               supportsTools,
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
            var judgeSnapshot = CreateJudgeSnapshot(project, judge);
            var snapshot = _snapshots.Create(new BenchmarkRuntimeSnapshotInput(project.Id,
                definition.Id,
                definition.Version,
                exactCoreTask,
                project.ContextTokens,
                eligible,
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

    private static BenchmarkJudgeSnapshotV1 CreateJudgeSnapshot(BenchmarkProjectRecord project, InstalledModelSnapshot? judge)
    {
        if (!project.JudgeEnabled)
        {
            return new BenchmarkJudgeSnapshotV1(false, null, project.JudgePromptVersion, project.JudgeOutputSchemaVersion, null,
                Hash(new { project.JudgePromptVersion, project.JudgeOutputSchemaVersion, Enabled = false }));
        }

        var model = ToSnapshot(judge ?? throw new BenchmarkEligibilityException("The selected judge model is not installed."));
        return new BenchmarkJudgeSnapshotV1(true,
            model,
            project.JudgePromptVersion,
            project.JudgeOutputSchemaVersion,
            project.JudgeContextTokens,
            Hash(new { project.JudgePromptVersion, project.JudgeOutputSchemaVersion, project.JudgeContextTokens, model.ModelContentFingerprint }));
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
    public static void Validate(InstalledModelSnapshot snapshot, string role)
    {
        if (!string.Equals(snapshot.ProviderName, "llamacpp", StringComparison.OrdinalIgnoreCase)
            || snapshot.Role != GgufRole.Chat
            || snapshot.Members.Any(static member => member.Role == InstalledModelPhysicalMemberRole.Projector))
        {
            throw new BenchmarkEligibilityException($"The selected {role} model is not an eligible local text-generation GGUF.");
        }
    }
}
