namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Text;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class BenchmarkRuntimeSnapshotTests
{
    [Test]
    public void Eligibility_StripsMandatoryAskUserOnly()
    {
        var policy = new BenchmarkEligibilityPolicy();
        var runtime = CreateRuntime([
            Tool(AskUserTool.ToolName, ToolCategory.Orchestration, requiresApproval: true),
            Tool("read_clock", ToolCategory.ReadLocal, requiresApproval: false)
        ]);

        var result = policy.Apply(runtime);

        AssertEx.Equal(expected: 1, result.AllowedTools.Count);
        AssertEx.Equal("read_clock", result.AllowedTools[0].Name);
    }

    [Test]
    public void Eligibility_RejectsApprovalOrNonReadTools()
    {
        var policy = new BenchmarkEligibilityPolicy();
        _ = AssertEx.Throws<BenchmarkEligibilityException>(() => policy.Apply(CreateRuntime([Tool("write", ToolCategory.WriteExecute, requiresApproval: false)])));
        _ = AssertEx.Throws<BenchmarkEligibilityException>(() => policy.Apply(CreateRuntime([Tool("read", ToolCategory.ReadLocal, requiresApproval: true)])));
    }

    [Test]
    public void Snapshot_RoundTripsCompleteRuntimeAndRejectsTamper()
    {
        var factory = new BenchmarkRuntimeSnapshotFactory(new BenchmarkEligibilityPolicy());
        var model = CreateModel();
        var snapshot = factory.Create(new BenchmarkRuntimeSnapshotInput(Guid.NewGuid(), Guid.NewGuid(), 4, "exact task", 4096,
            CreateRuntime([Tool("read", ToolCategory.ReadLocal, requiresApproval: false)]),
            new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cpu, 4096, null, null, null, null, null, false,
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1),
            BenchmarkFrozenPolicies.DeterministicSampling(),
            model,
            new BenchmarkJudgeSnapshotV1(false, null, 1, 1, null, null, null, null, null, "sha256:" + new string('b', 64)),
            new BenchmarkFreezeDependencySetV1("agent", "playbook", "skills", "tools", "runtime", null), "test", 123));

        var payload = factory.Serialize(snapshot);
        var roundTrip = factory.Deserialize(payload);
        AssertEx.Equal(snapshot.ConfigurationHash, roundTrip.ConfigurationHash);
        AssertEx.Equal(LocalModelOrigin.Imported, roundTrip.PrimaryModel.Origin);
        AssertEx.Equal(model.ModelContentFingerprint, roundTrip.PrimaryModel.ModelContentFingerprint);

        var tampered = Encoding.UTF8.GetString(payload).Replace("exact task", "other task", StringComparison.Ordinal);
        _ = AssertEx.Throws<BenchmarkSnapshotException>(() => factory.Deserialize(Encoding.UTF8.GetBytes(tampered)));

        _ = AssertEx.Throws<BenchmarkSnapshotException>(() => factory.Create(new BenchmarkRuntimeSnapshotInput(Guid.NewGuid(),
            Guid.NewGuid(),
            4,
            "exact task",
            4096,
            CreateRuntime([]),
            new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cpu, 4096, null, null, null, null, null, false,
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1),
            BenchmarkFrozenPolicies.DeterministicSampling(),
            model,
            new BenchmarkJudgeSnapshotV1(false, null, 2, 1, null, null, null, null, null, "hash"),
            new BenchmarkFreezeDependencySetV1("agent", "playbook", "skills", "tools", "runtime", null),
            "test",
            123)));
    }

    private static ResolvedAgentRuntime CreateRuntime(IReadOnlyList<AllowedToolDto> tools) =>
        new("prompt", tools, null, null, 4, Guid.NewGuid(), "Agent", Kind: AgentDefinitionKind.Single);

    private static AllowedToolDto Tool(string name, ToolCategory category, bool requiresApproval) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ApiSide,
            Category = category,
            RequiresApproval = requiresApproval
        };

    private static BenchmarkInstalledModelSnapshotV1 CreateModel()
    {
        var v1 = "v1:" + new string('a', 64);
        return new BenchmarkInstalledModelSnapshotV1("model.gguf", v1, [new BenchmarkRegistryAliasSnapshotV1("model.gguf", v1)], v1,
            [
                new BenchmarkPhysicalMemberSnapshotV1("model.gguf", InstalledModelPhysicalMemberRole.Weight, 12, new string('c', 64), ["model.gguf"], true, 1,
                    "sha256:" + new string('c', 64) + ":12")
            ],
            v1, LocalModelOrigin.Imported, "llamacpp", "revision", null, null, "model.gguf", "Q4_K_M", "chat", v1);
    }
}
