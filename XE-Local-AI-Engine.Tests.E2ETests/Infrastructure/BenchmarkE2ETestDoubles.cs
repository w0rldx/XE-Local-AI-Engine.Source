namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The two benchmark seams the browser suite substitutes, and the single installed-model identity they agree on.
///     <para>
///         Benchmark execution is bound to a real <c>llama-server</c> process (installed GGUF files, GPU variant
///         selection, capacity admission), and the browser host deliberately runs with <c>RemoveAll&lt;IHostedService&gt;</c>
///         and an NSubstitute <c>llamacpp</c> provider — so no run and no judging can ever execute in it. These two
///         doubles are what let the SHIPPED judge-policy and ranking surfaces be driven end to end anyway: the policy
///         builder resolves a stable model identity, and a judge activation resolves a launch vector instead of
///         recording "runtime unresolved" and failing every attempt it enqueues. Everything downstream of them — the
///         policy hash, the revision/attempt rows, the ranking SQL, every endpoint and the whole page — is production
///         code. The parts that genuinely need a GPU (the primary invocation and the judge model call) are driven by
///         the test through the real store transitions instead, and are validated for real by the live 5090 pass.
///     </para>
/// </summary>
public static class BenchmarkE2ETestDoubles
{
    /// <summary>The one model name the benchmark surfaces resolve in browser E2E, primary and judge alike.</summary>
    public const string ModelName = "e2e-benchmark-model.gguf";

    /// <summary>Aggregate content fingerprint of <see cref="ModelName" />; pinned so a policy hash is reproducible.</summary>
    public const string ModelContentFingerprint = "v1:" + Sha;

    private const string Sha = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>The installed-model identity the substituted lease hands out. Chat + llamacpp + no projector: judge-eligible.</summary>
    public static InstalledModelSnapshot InstalledModel { get; } = new(ModelName,
        "registry-rev-1",
        RegistryAliases: [],
        "alias-set-hash",
        [
            new InstalledModelPhysicalMember("models/e2e-benchmark-model.gguf",
                InstalledModelPhysicalMemberRole.Weight,
                SizeBytes: 1024,
                Sha,
                MemberFingerprint: "v1:" + Sha,
                OwningAliases: [ModelName],
                Required: true,
                MetadataSchemaVersion: 1)
        ],
        "member-set-hash",
        LocalModelOrigin.Imported,
        "llamacpp",
        ProviderMappingRevision: null,
        "e2e/benchmark-model",
        "main",
        "Q4_K_M",
        GgufRole.Chat,
        ModelContentFingerprint);

    /// <summary>The same identity in the benchmark snapshot shape, which is what a judge policy hashes over.</summary>
    public static BenchmarkInstalledModelSnapshotV1 SnapshotV1 { get; } = new(ModelName,
        "registry-rev-1",
        RegistryAliases: [],
        "alias-set-hash",
        [
            new BenchmarkPhysicalMemberSnapshotV1("models/e2e-benchmark-model.gguf",
                InstalledModelPhysicalMemberRole.Weight,
                SizeBytes: 1024,
                Sha,
                OwningAliases: [ModelName],
                Required: true,
                MetadataSchemaVersion: 1,
                "v1:" + Sha)
        ],
        "member-set-hash",
        LocalModelOrigin.Imported,
        "llamacpp",
        ProviderMappingRevision: null,
        "e2e/benchmark-model",
        "main",
        "e2e-benchmark-model.gguf",
        "Q4_K_M",
        "chat",
        ModelContentFingerprint);

    /// <summary>Builds the policy exactly as <c>BenchmarkProjectService</c> would for this node's installed judge model.</summary>
    public static BenchmarkJudgePolicyV1 Policy(BenchmarkJudgeRubricV1 rubric, string? referenceAnswer, int contextTokens = 4096) =>
        new(BenchmarkJudgePolicyModelV1.FromSnapshot(SnapshotV1),
            contextTokens,
            BenchmarkJudgePolicyVersions.PromptVersion,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            rubric,
            referenceAnswer);

    /// <summary>Hands every caller the one <see cref="InstalledModel" />, so policy building and judge eligibility resolve.</summary>
    public sealed class LeaseProvider : IBenchmarkInstalledModelLeaseProvider
    {
        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken) =>
            string.Equals(modelName, ModelName, StringComparison.Ordinal)
                ? Task.FromResult<IBenchmarkInstalledModelLease>(new Lease())
                : throw new KeyNotFoundException("The installed model was not found.");

        private sealed class Lease : IBenchmarkInstalledModelLease
        {
            public InstalledModelSnapshot Snapshot => InstalledModel;

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     Resolves a fixed CPU launch vector. Without it a judge activation records a sanitized "runtime unresolved"
    ///     reason and every attempt it enqueues is inserted terminal-Failed, which would make the queued -&gt; succeeded
    ///     lifecycle the ranking is built on unreachable from a browser test.
    /// </summary>
    public sealed class JudgeRuntimeResolver : IBenchmarkJudgeRuntimeResolver
    {
        public Task<BenchmarkJudgeRuntimeResolution> ResolveAsync(BenchmarkJudgePolicyV1 policy, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var runtime = new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cpu,
                policy.RequestedContextTokens,
                GpuLayers: 0,
                TensorSplit: null,
                OverrideTensor: null,
                "f16",
                "f16",
                FlashAttention: false,
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1);
            return Task.FromResult(new BenchmarkJudgeRuntimeResolution(new BenchmarkJudgeRuntimeV1(BenchmarkJudgeRuntimeV1.CurrentSchemaVersion,
                    SnapshotV1,
                    policy.RequestedContextTokens,
                    runtime,
                    BenchmarkFrozenPolicies.DeterministicSampling()),
                new BenchmarkRunLaunchIntent("cpu", "f16", "auto", "cpu-variant", "off", "e2e-intended-identity", null)));
        }
    }

    /// <summary>
    ///     The benchmark catalog over the one substituted model. Without it <c>GET eligible-models</c> is empty, the
    ///     judge-model <c>Select</c> renders with no option to hold its value, and — because that control is
    ///     <c>required</c> — native form validation silently refuses every judge save in the editor. The agent half
    ///     lists the node's Single definitions without the runtime-resolution filter the real service applies; the SPA
    ///     never calls that endpoint, and a benchmark host with no llama.cpp runtime cannot resolve an agent anyway.
    /// </summary>
    public sealed class CatalogService(IAgentDefinitionStore agentDefinitions) : IBenchmarkCatalogService
    {
        public async Task<IReadOnlyList<BenchmarkEligibleAgent>> ListEligibleAgentsAsync(string modelName,
            CancellationToken cancellationToken = default)
        {
            var definitions = await agentDefinitions.ListAsync(cancellationToken).ConfigureAwait(false);
            return definitions.Where(static definition => definition.Kind == AgentDefinitionKind.Single)
                              .Select(static definition => new BenchmarkEligibleAgent(definition.Id, definition.Name, definition.Version))
                              .ToArray();
        }

        public Task<IReadOnlyList<BenchmarkEligibleModel>> ListEligibleModelsAsync(int? contextTokens,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkEligibleModel>>([
                new BenchmarkEligibleModel(ModelName,
                    MaxContextTokens: 32768,
                    EffectiveContextTokens: null,
                    LocalModelOrigin.Imported,
                    ModelContentFingerprint,
                    SupportsTools: false)
            ]);
    }
}
