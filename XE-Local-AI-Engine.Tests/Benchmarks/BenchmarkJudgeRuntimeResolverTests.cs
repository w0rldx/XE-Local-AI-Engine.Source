namespace XE_Local_AI_Engine.Tests.Benchmarks;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The runtime resolver applies the same rubric-aware judge eligibility the policy was saved under: a rubric of
///     verifiable criteria only never spawns the judge, so an auxiliary-asset model must not fail the judging there.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BenchmarkJudgeRuntimeResolverTests
{
    private const string JudgeModel = "judge-model";

    [Test]
    public async Task Resolve_AnAuxiliaryAssetJudge_ForAnAllVerifiableRubric_PassesEligibilityAndReachesTheLaunch()
    {
        var launchResolver = Substitute.For<IBenchmarkPhaseLaunchResolver>();
        // The launch is the step AFTER eligibility; a sentinel there proves the gate let the model through.
        launchResolver.InspectAsync(Arg.Any<CancellationToken>()).Returns<LlamaServerLaunchCapabilities?>(_ => throw new InvalidOperationException("reached the launch"));
        var resolver = Resolver(carriesProjector: true, launchResolver);

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(Policy(carriesProjector: true, BenchmarkJudgeRubricDefaults.Verifiable()), CancellationToken.None));

        AssertEx.Equal("reached the launch", exception.Message);
    }

    [Test]
    public async Task Resolve_AnAuxiliaryAssetJudge_ForAPairwisePolicy_IsRefusedWhateverTheRubric()
    {
        var launchResolver = Substitute.For<IBenchmarkPhaseLaunchResolver>();
        var resolver = Resolver(carriesProjector: true, launchResolver);

        var exception = await AssertEx.ThrowsAsync<BenchmarkEligibilityException>(() =>
            resolver.ResolveAsync(Policy(carriesProjector: true, BenchmarkJudgeRubricDefaults.Verifiable()) with
                {
                    Mode = BenchmarkJudgePolicyModes.Pairwise
                },
                CancellationToken.None));

        AssertEx.True(exception.Message.Contains("auxiliary asset", StringComparison.Ordinal), exception.Message);
        _ = launchResolver.DidNotReceive().InspectAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Resolve_AnAuxiliaryAssetJudge_ForAModelDecidedRubric_IsRefusedBeforeTheLaunch()
    {
        var launchResolver = Substitute.For<IBenchmarkPhaseLaunchResolver>();
        var resolver = Resolver(carriesProjector: true, launchResolver);

        var exception = await AssertEx.ThrowsAsync<BenchmarkEligibilityException>(() =>
            resolver.ResolveAsync(Policy(carriesProjector: true, BenchmarkJudgeRubricDefaults.Default()), CancellationToken.None));

        AssertEx.True(exception.Message.Contains("auxiliary asset", StringComparison.Ordinal), exception.Message);
        _ = launchResolver.DidNotReceive().InspectAsync(Arg.Any<CancellationToken>());
    }

    private static BenchmarkJudgeRuntimeResolver Resolver(bool carriesProjector, IBenchmarkPhaseLaunchResolver launchResolver)
    {
        var leases = Substitute.For<IBenchmarkInstalledModelLeaseProvider>();
        leases.AcquireAsync(JudgeModel, Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult<IBenchmarkInstalledModelLease>(new JudgeLease(Installed(carriesProjector))));
        return new BenchmarkJudgeRuntimeResolver(leases, launchResolver, Substitute.For<IModelClassificationStore>());
    }

    private static BenchmarkJudgePolicyV1 Policy(bool carriesProjector, BenchmarkJudgeRubricV1 rubric) =>
        new(BenchmarkJudgePolicyModelV1.FromSnapshot(BenchmarkInstalledModelSnapshotMapper.ToSnapshot(Installed(carriesProjector))),
            4096,
            BenchmarkJudgePolicyVersions.PromptVersion,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            rubric,
            ReferenceAnswer: null);

    private static InstalledModelSnapshot Installed(bool carriesProjector)
    {
        var revision = $"v1:{new string('a', count: 64)}";
        InstalledModelPhysicalMember[] members = carriesProjector
            ? [Member(JudgeModel, InstalledModelPhysicalMemberRole.Weight), Member($"{JudgeModel}-mmproj", InstalledModelPhysicalMemberRole.Projector)]
            : [Member(JudgeModel, InstalledModelPhysicalMemberRole.Weight)];
        return new InstalledModelSnapshot(JudgeModel,
            revision,
            [],
            revision,
            members,
            revision,
            LocalModelOrigin.Imported,
            "llamacpp",
            "map-revision",
            "repo/judge",
            "revision",
            "Q4_K_M",
            GgufRole.Chat,
            revision);

        static InstalledModelPhysicalMember Member(string path, InstalledModelPhysicalMemberRole role) =>
            new(path, role, 12, new string('b', count: 64), $"sha256:{new string('b', count: 64)}:12", [JudgeModel], true, null);
    }

    private sealed class JudgeLease : IBenchmarkInstalledModelLease
    {
        public JudgeLease(InstalledModelSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public InstalledModelSnapshot Snapshot { get; }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
