namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The KV-cache work adds no snapshot schema version (D9): a run frozen before it must still deserialize and
///     re-serialize to the same bytes. The literal below is a complete v1 payload — if a member is added, renamed,
///     reordered or re-typed anywhere in the snapshot records, this test fails, which is the intended signal that
///     legacy rows would stop replaying.
/// </summary>
public sealed class BenchmarkRuntimeSnapshotV1CompatibilityTests
{
    private const string LiteralV1Snapshot =
        """
        {"schemaVersion":1,"projectId":"11111111-1111-1111-1111-111111111111","agentDefinitionId":"22222222-2222-2222-2222-222222222222","agentVersion":3,"coreTask":"Summarize the release notes.","requestedContextTokens":4096,"resolvedRuntime":{"resolvedSystemPrompt":"prompt","allowedTools":[],"modelProfile":null,"reasoningEffort":null,"agentDefinitionVersion":3,"agentDefinitionId":"22222222-2222-2222-2222-222222222222","agentName":"Agent","skills":null,"playbookEnabled":false,"memoryExtractionEnabled":true,"effectiveModelIsCloud":false,"kind":0,"customTools":null},"primaryRuntime":{"variant":1,"contextTokens":8192,"gpuLayers":32,"tensorSplit":null,"overrideTensor":null,"kvTypeK":"q8_0","kvTypeV":"q8_0","flashAttention":true,"launchPolicy":{"version":1,"chatCacheReuse":0,"chatCacheRamMiB":0,"speculativeDecodingEnabled":false,"isSupported":true}},"primarySampling":{"temperature":0,"topP":null,"topK":null,"minP":null,"maxOutputTokens":null,"repeatPenalty":null,"repeatLastN":null,"presencePenalty":null,"frequencyPenalty":null,"stop":[],"seedPolicy":"fixed","seedValue":"0"},"primaryModel":{"modelName":"bartowski/Model-GGUF:Q4_K_M","registryRevision":"v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","registryAliases":[],"registryAliasSetHash":"v1:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","members":[{"relativePath":"Model-Q4_K_M.gguf","role":0,"sizeBytes":4096,"sha256":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","owningAliases":["bartowski/Model-GGUF:Q4_K_M"],"required":true,"metadataSchemaVersion":1,"memberFingerprint":null}],"physicalMemberSetHash":"v1:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","origin":null,"providerName":"llamacpp","providerMappingRevision":null,"repositoryId":null,"repositoryRevision":null,"sourceFileName":null,"quantization":"Q4_K_M","role":"chat","modelContentFingerprint":"v1:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"},"judge":{"enabled":false,"model":null,"promptVersion":1,"outputSchemaVersion":1,"requestedContextTokens":null,"systemPrompt":null,"outputSchemaJson":null,"runtime":null,"sampling":null,"configurationHash":"v1:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"},"dependencies":{"agentDependencyHash":"v1:1111111111111111111111111111111111111111111111111111111111111111","playbookCohortHash":"v1:2222222222222222222222222222222222222222222222222222222222222222","skillAssignmentSetHash":"v1:3333333333333333333333333333333333333333333333333333333333333333","customToolAssignmentSetHash":"v1:4444444444444444444444444444444444444444444444444444444444444444","primaryRuntimeConfigurationHash":"v1:5555555555555555555555555555555555555555555555555555555555555555","judgeRuntimeConfigurationHash":null},"applicationVersion":"0.1.0","createdAtUtc":1700000000000,"configurationHash":"sha256:13cf3d7c6f1672826251a09b6d7170c097e15ba93b8adaaf5d221031b2220e27"}
        """;

    [Test]
    public void Serialize_V1Snapshot_MatchesTheLiteralWireShape()
    {
        var factory = new BenchmarkRuntimeSnapshotFactory(new BenchmarkEligibilityPolicy());

        var serialized = Encoding.UTF8.GetString(factory.Serialize(factory.Create(Input())));

        AssertEx.Equal(LiteralV1Snapshot, serialized, "the v1 snapshot wire shape is frozen — no schema v2");
    }

    [Test]
    public void Deserialize_LiteralV1Snapshot_RoundTripsByteIdentically()
    {
        var factory = new BenchmarkRuntimeSnapshotFactory(new BenchmarkEligibilityPolicy());
        var payload = Encoding.UTF8.GetBytes(LiteralV1Snapshot);

        var snapshot = factory.Deserialize(payload);
        var reserialized = factory.Serialize(snapshot);

        AssertEx.Equal(expected: 1, snapshot.SchemaVersion);
        AssertEx.Equal("q8_0", snapshot.PrimaryRuntime.KvTypeK);
        AssertEx.True(payload.AsSpan().SequenceEqual(reserialized), "a v1 payload must re-serialize to the same bytes");
    }

    private static BenchmarkRuntimeSnapshotInput Input() =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AgentVersion: 3,
            "Summarize the release notes.",
            RequestedContextTokens: 4096,
            new ResolvedAgentRuntime("prompt", [], null, null, 3, Guid.Parse("22222222-2222-2222-2222-222222222222"), "Agent"),
            new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cuda,
                ContextTokens: 8192,
                GpuLayers: 32,
                TensorSplit: null,
                OverrideTensor: null,
                "q8_0",
                "q8_0",
                FlashAttention: true,
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1),
            BenchmarkFrozenPolicies.DeterministicSampling(),
            Model(),
            new BenchmarkJudgeSnapshotV1(Enabled: false, null, 1, 1, null, null, null, null, null, Hash('e')),
            new BenchmarkFreezeDependencySetV1(Hash('1'), Hash('2'), Hash('3'), Hash('4'), Hash('5'), null),
            "0.1.0",
            CreatedAtUtc: 1700000000000);

    private static BenchmarkInstalledModelSnapshotV1 Model() =>
        new("bartowski/Model-GGUF:Q4_K_M",
            Hash('a'),
            [],
            Hash('b'),
            [
                new BenchmarkPhysicalMemberSnapshotV1("Model-Q4_K_M.gguf",
                    InstalledModelPhysicalMemberRole.Weight,
                    SizeBytes: 4096,
                    new string('f', 64),
                    ["bartowski/Model-GGUF:Q4_K_M"],
                    Required: true,
                    MetadataSchemaVersion: 1,
                    MemberFingerprint: null)
            ],
            Hash('c'),
            null,
            "llamacpp",
            null,
            null,
            null,
            null,
            "Q4_K_M",
            "chat",
            Hash('d'));

    private static string Hash(char value) => $"v1:{new string(value, 64)}";
}
