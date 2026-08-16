namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkProjectServiceTests
{
    private static readonly Guid ProjectId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RevisionId = new("22222222-2222-2222-2222-222222222222");

    [Test]
    public async Task Create_PreservesExactTaskPayload()
    {
        var context = new ServiceContext();

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "  exact task  ", 4096, context.AgentId));

        AssertEx.Equal("  exact task  ", BenchmarkProjectService.DecodeCoreTask(AssertEx.NotNull(context.CreatedInput).CoreTaskJson.Span));
        _ = context.Store.DidNotReceive().ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_RejectsUnsupportedContextAndNonSingleAgentBeforePersistence()
    {
        var context = new ServiceContext(agentKind: AgentDefinitionKind.Orchestrator);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 1234, context.AgentId)));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, context.AgentId)));

        _ = context.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WithJudge_ActivatesAHashedPolicyRevisionCarryingTheDefaultRubric()
    {
        var context = new ServiceContext();

        _ = await context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId,
            "Benchmark",
            "task",
            4096,
            context.AgentId,
            new BenchmarkJudgePolicyDraft("  judge-model  ", 8192)));

        AssertEx.True(context.ActivatedPolicyJson is not null, "The activation must carry the canonical policy bytes.");
        var policy = BenchmarkJudgeSerialization.DeserializePolicy(context.ActivatedPolicyJson!.Value.Span);
        AssertEx.Equal(8192, policy.RequestedContextTokens);
        AssertEx.Equal(BenchmarkJudgePolicyVersions.PromptVersion, policy.PromptVersion);
        AssertEx.Equal(BenchmarkJudgeRubricDefaults.Default().Criteria.Count, policy.Rubric.Criteria.Count);
        AssertEx.Equal(BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy), context.ActivatedHash);
    }

    [Test]
    public async Task Create_WithAnAuxiliaryAssetJudgeModel_IsRejectedBeforePersistence()
    {
        var context = new ServiceContext(judgeCarriesProjector: true);

        var exception = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            context.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId,
                "Benchmark",
                "task",
                4096,
                context.AgentId,
                new BenchmarkJudgePolicyDraft("judge-model", 4096))));

        AssertEx.True(exception.Message.Contains("auxiliary asset", StringComparison.Ordinal),
            "A judging from an aux-asset model can never be ranked, so it is refused at the policy.");
        _ = context.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WithAMissingOrIncompleteJudge_IsRejectedBeforePersistence()
    {
        var missing = new ServiceContext(judgeModelInstalled: false);
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            missing.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, missing.AgentId, new BenchmarkJudgePolicyDraft("gone", 4096))));

        var incomplete = new ServiceContext();
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            incomplete.Service.CreateAsync(new BenchmarkProjectDraft(ProjectId, "Benchmark", "task", 4096, incomplete.AgentId, new BenchmarkJudgePolicyDraft("   ", 4096))));

        _ = missing.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<CancellationToken>());
        _ = incomplete.Store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_WithTheSameHash_IsANoOp()
    {
        var context = new ServiceContext();
        var draft = new BenchmarkJudgePolicyDraft("judge-model", 4096);
        context.SetCurrentRevision(await context.BuildPolicyHashAsync(draft));

        _ = await context.Service.UpdateJudgePolicyAsync(ProjectId, 1, draft, confirmRejudge: false);

        _ = context.Store.DidNotReceive().ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = context.Store.DidNotReceive().EnqueueJudgeAttemptAsync(Arg.Any<BenchmarkEnqueueJudgeAttemptCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_WithADifferentHashOnAProjectWithRuns_RequiresConfirmation()
    {
        var context = new ServiceContext(runCount: 2);
        context.SetCurrentRevision("f" + new string('0', count: 63));

        var exception = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() =>
            context.Service.UpdateJudgePolicyAsync(ProjectId, 1, new BenchmarkJudgePolicyDraft("judge-model", 4096), confirmRejudge: false));

        AssertEx.Equal("RejudgeRequired", exception.Code);
        _ = context.Store.DidNotReceive().ActivateJudgePolicyAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateJudgePolicy_WhenConfirmed_ActivatesAndEnqueuesOneAttemptPerSucceededRun()
    {
        var context = new ServiceContext(runCount: 2, succeededRunIds: [Guid.NewGuid(), Guid.NewGuid()]);
        context.SetCurrentRevision("f" + new string('0', count: 63));

        _ = await context.Service.UpdateJudgePolicyAsync(ProjectId, 1, new BenchmarkJudgePolicyDraft("judge-model", 4096), confirmRejudge: true);

        AssertEx.Equal(expected: 2, context.Enqueued.Count);
        AssertEx.True(context.Enqueued.All(static command => command.Force), "An activation covers the complete eligible set, already-applied guard included.");
        AssertEx.True(context.Enqueued.All(static command => command.PolicyRevisionId == RevisionId));
        AssertEx.True(context.Enqueued.All(static command => command.RuntimeJson is not null), "A resolvable runtime is frozen onto each attempt.");
        AssertEx.Equal(expected: 1, context.RuntimeResolutions, "The runtime depends only on the policy, so it is resolved once per activation.");
    }

    [Test]
    public async Task UpdateJudgePolicy_WhenTheJudgeRuntimeCannotBeResolved_EnqueuesAttemptsCarryingTheReason()
    {
        var context = new ServiceContext(runCount: 1, succeededRunIds: [Guid.NewGuid()], judgeRuntimeResolves: false);
        context.SetCurrentRevision("f" + new string('0', count: 63));

        _ = await context.Service.UpdateJudgePolicyAsync(ProjectId, 1, new BenchmarkJudgePolicyDraft("judge-model", 4096), confirmRejudge: true);

        var enqueued = AssertEx.NotNull(context.Enqueued.SingleOrDefault());
        AssertEx.Null(enqueued.RuntimeJson, "An unresolvable runtime is a failed attempt, not a refused activation.");
        AssertEx.Equal("judge runtime is unavailable", enqueued.RuntimeUnresolvedReason);
    }

    [Test]
    public async Task RejudgeRun_WithoutACurrentPolicy_IsRefused()
    {
        var context = new ServiceContext();

        var exception = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => context.Service.RejudgeRunAsync(Guid.NewGuid(), 1, force: false));

        AssertEx.Equal("JudgeDisabled", exception.Code);
    }

    private sealed class ServiceContext
    {
        public ServiceContext(AgentDefinitionKind agentKind = AgentDefinitionKind.Single,
            bool judgeModelInstalled = true,
            bool judgeCarriesProjector = false,
            bool judgeRuntimeResolves = true,
            int runCount = 0,
            IReadOnlyList<Guid>? succeededRunIds = null)
        {
            AgentId = Guid.NewGuid();
            Store = Substitute.For<IBenchmarkStore>();
            Store.CreateProjectAsync(Arg.Do<BenchmarkProjectInput>(input => CreatedInput = input), Arg.Any<CancellationToken>())
                 .Returns(call => Project(call.Arg<BenchmarkProjectInput>()));
            Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_ => CurrentProject());
            Store.CountRunsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(runCount);
            Store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_ => _currentRevision);
            Store.GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => Run(call.Arg<Guid>()));
            Store.ActivateJudgePolicyAsync(ProjectId, Arg.Any<long>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(call =>
                 {
                     ActivatedPolicyJson = call.ArgAt<ReadOnlyMemory<byte>>(2);
                     ActivatedHash = call.ArgAt<string>(3);
                     return new BenchmarkJudgePolicyActivation(Revision(ActivatedHash), WasCreated: true, succeededRunIds ?? []);
                 });
            Store.EnqueueJudgeAttemptAsync(Arg.Do<BenchmarkEnqueueJudgeAttemptCommand>(command => Enqueued.Add(command)), Arg.Any<CancellationToken>())
                 .Returns(call => Attempt(call.Arg<BenchmarkEnqueueJudgeAttemptCommand>()));

            var agents = Substitute.For<IAgentDefinitionStore>();
            agents.GetByIdAsync(AgentId, Arg.Any<CancellationToken>()).Returns(Definition(AgentId, agentKind));

            Models = Substitute.For<IBenchmarkInstalledModelLeaseProvider>();
            if (judgeModelInstalled)
            {
                Models.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                      .Returns(call => Task.FromResult<IBenchmarkInstalledModelLease>(new JudgeLease(Installed(call.ArgAt<string>(0), judgeCarriesProjector))));
            }
            else
            {
                Models.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                      .Returns<Task<IBenchmarkInstalledModelLease>>(_ => throw new KeyNotFoundException());
            }

            var runtimes = Substitute.For<IBenchmarkJudgeRuntimeResolver>();
            runtimes.ResolveAsync(Arg.Any<BenchmarkJudgePolicyV1>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        RuntimeResolutions++;
                        return judgeRuntimeResolves
                            ? Resolution(call.Arg<BenchmarkJudgePolicyV1>())
                            : throw new BenchmarkEligibilityException("judge runtime is unavailable");
                    });

            Service = new BenchmarkProjectService(Store, agents, Models, runtimes);
        }

        public Guid AgentId { get; }
        public IBenchmarkStore Store { get; }
        public IBenchmarkInstalledModelLeaseProvider Models { get; }
        public IBenchmarkProjectService Service { get; }
        public BenchmarkProjectInput? CreatedInput { get; private set; }
        public ReadOnlyMemory<byte>? ActivatedPolicyJson { get; private set; }
        public string? ActivatedHash { get; private set; }
        public int RuntimeResolutions { get; private set; }
        public List<BenchmarkEnqueueJudgeAttemptCommand> Enqueued { get; } = [];

        private BenchmarkJudgePolicyRevisionRecord? _currentRevision;

        public void SetCurrentRevision(string policyHash) =>
            _currentRevision = Revision(policyHash);

        /// <summary>The hash the service computes for <paramref name="draft" />, built the same way it does.</summary>
        public async Task<string> BuildPolicyHashAsync(BenchmarkJudgePolicyDraft draft)
        {
            await using var lease = await Models.AcquireAsync(draft.ModelName, CancellationToken.None).ConfigureAwait(false);
            return BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(new BenchmarkJudgePolicyV1(
                BenchmarkJudgePolicyModelV1.FromSnapshot(BenchmarkInstalledModelSnapshotMapper.ToSnapshot(lease.Snapshot)),
                draft.ContextTokens,
                BenchmarkJudgePolicyVersions.PromptVersion,
                BenchmarkJudgePolicyVersions.OutputSchemaVersion,
                BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
                draft.Rubric ?? BenchmarkJudgeRubricDefaults.Default(),
                draft.ReferenceAnswer));
        }

        private static BenchmarkProjectRecord CurrentProject() =>
            new(ProjectId, "Benchmark", Encoding.UTF8.GetBytes("\"task\""), 4096, Guid.NewGuid(), JudgeEnabled: true, RevisionId, IsFrozen: true, 1, 1, 1);

        private static BenchmarkJudgePolicyRevisionRecord Revision(string? policyHash) =>
            new(RevisionId, ProjectId, 1, Encoding.UTF8.GetBytes("{}"), policyHash ?? new string('a', count: 64), null, 1, 1);

        private static BenchmarkJudgeRuntimeResolution Resolution(BenchmarkJudgePolicyV1 policy) =>
            new(new BenchmarkJudgeRuntimeV1(BenchmarkJudgeRuntimeV1.CurrentSchemaVersion,
                    BenchmarkInstalledModelSnapshotMapper.ToSnapshot(Installed(policy.Model.ModelName, carriesProjector: false)),
                    policy.RequestedContextTokens,
                    new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cpu, policy.RequestedContextTokens, null, null, null, null, null, false,
                        LlamaServerBenchmarkLaunchPolicy.DeterministicV1),
                    BenchmarkFrozenPolicies.DeterministicSampling()),
                new BenchmarkRunLaunchIntent("cpu", "f16", "auto", "cpu-variant", "auto", new string('c', count: 64), null));

        private static BenchmarkRunRecord Run(Guid runId) =>
            new(runId, ProjectId, new byte[]
                {
                    1
                }, "model.gguf", LocalModelOrigin.Imported, $"v1:{new string('a', count: 64)}", "Agent", 1, 4096,
                BenchmarkPrimaryStatus.Succeeded, null, null, null, null, null, 0, null, null, 3, 1, 1, null, 1);

        private static BenchmarkJudgeAttemptRecord Attempt(BenchmarkEnqueueJudgeAttemptCommand command) =>
            new(Guid.NewGuid(), command.RunId, 1, command.PolicyRevisionId, 1, command.RuntimeJson, null,
                command.RuntimeJson is null ? BenchmarkJudgeAttemptStatus.Failed : BenchmarkJudgeAttemptStatus.Queued,
                null, null, command.RuntimeUnresolvedReason, 1, null, null, 1);

        private static BenchmarkProjectRecord Project(BenchmarkProjectInput input) =>
            new(input.Id, input.Name, input.CoreTaskJson, input.ContextTokens, input.AgentDefinitionId, JudgeEnabled: false,
                CurrentJudgePolicyRevisionId: null, IsFrozen: false, 1, 1, 1);

        private static AgentDefinitionRecord Definition(Guid id, AgentDefinitionKind kind) =>
            new(id, "Agent", null, "instructions", null, null, kind, [], new Dictionary<string, bool>(), null, 1, 1, 1);

        private static InstalledModelSnapshot Installed(string modelName, bool carriesProjector)
        {
            var revision = $"v1:{new string('a', count: 64)}";
            InstalledModelPhysicalMember[] members = carriesProjector
                ?
                [
                    Member(modelName, InstalledModelPhysicalMemberRole.Weight),
                    Member($"{modelName}-mmproj", InstalledModelPhysicalMemberRole.Projector)
                ]
                : [Member(modelName, InstalledModelPhysicalMemberRole.Weight)];
            return new InstalledModelSnapshot(modelName,
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

            InstalledModelPhysicalMember Member(string path, InstalledModelPhysicalMemberRole role) =>
                new(path, role, 12, new string('b', count: 64), $"sha256:{new string('b', count: 64)}:12", [modelName], true, null);
        }

        private sealed class JudgeLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
        {
            public InstalledModelSnapshot Snapshot { get; } = snapshot;

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }
}
