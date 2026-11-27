namespace XE_Local_AI_Engine.Tests.Benchmarks;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Judge-run fixtures and doubles shared by the benchmark judge, comparison, review and pairwise suites.</summary>
internal static class BenchmarkJudgeTestSupport
{
    internal static BenchmarkJudgePolicyV1 Policy(int? promptVersion = null, BenchmarkJudgeRubricV1? rubric = null) =>
        new(new BenchmarkJudgePolicyModelV1("judge.gguf", V1('c'), [new string('b', 64)]),
            4096,
            promptVersion ?? BenchmarkJudgePolicyVersions.PromptVersion,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            rubric ?? new BenchmarkJudgeRubricV1(BenchmarkJudgePolicyVersions.RubricVersion,
            [
                new BenchmarkJudgeRubricCriterionV1("correctness", "Correctness", "Is the answer right?", 40)
            ]),
            ReferenceAnswer: null);

    internal sealed class StubEnvironmentFacts : IRuntimeEnvironmentFactsProvider
    {
        public Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct) =>
            Task.FromResult(new RuntimeEnvironmentFactsV1(1, null, null, null, 42, ["hardware"]));
    }

    internal static InvocationState State(Guid invocationId, string content) =>
        new()
        {
            InvocationId = invocationId,
            ConversationId = Guid.NewGuid(),
            Status = InvocationStatus.Completed,
            StreamedContent = content,
            StartedAt = DateTimeOffset.UnixEpoch,
            LastUpdatedAt = DateTimeOffset.UnixEpoch
        };

    internal static BenchmarkRunRecord Run(BenchmarkRuntimeSnapshotV1 snapshot, string judgeState, long version, string? primaryStopReason = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = snapshot.ProjectId,
            RuntimeSnapshotJson = new byte[]
            {
                1
            },
            PrimaryModelName = snapshot.PrimaryModel.ModelName,
            PrimaryModelOrigin = snapshot.PrimaryModel.Origin,
            ModelContentFingerprint = snapshot.PrimaryModel.ModelContentFingerprint,
            AgentName = "Agent",
            AgentDefinitionId = Guid.Empty,
            AgentVersion = 1,
            RequestedContextTokens = snapshot.RequestedContextTokens,
            PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
            EffectiveContextTokens = snapshot.RequestedContextTokens,
            DurationMs = 10,
            TotalTokens = 5,
            TokensPerSecond = 500,
            // A thinking model's stored transcript: reasoning parts around the visible answer. The judge must be shown
            // the answer only.
            OutputPartsJson = BenchmarkExecutionSerialization.SerializeParts([
                new BenchmarkOutputPart("reasoning", Content: "hidden chain of thought"),
                new BenchmarkOutputPart("output", Content: "answer"),
                new BenchmarkOutputPart("reasoning", Content: "more hidden thought")
            ]),
            LastStreamSequence = 1,
            UserScore = null,
            PrimaryErrorMessage = null,
            Version = version,
            CreatedAtUtc = 1,
            StartedAtUtc = 1,
            PrimaryCompletedAtUtc = 1,
            UpdatedAtUtc = 1,
            PrimaryLaunchIntent = null,
            PrimaryLaunchEvidence = null,
            PrimaryStopReason = primaryStopReason,
            Judge = new BenchmarkRunJudgeView
            {
                State = judgeState,
                AttemptId = null,
                Score = null,
                PolicyRevision = null,
                PolicyRevisionId = null,
                AttemptSequence = null,
                CohortGeneration = null,
                ExecutionKey = null,
                ErrorMessage = null,
                PolicyCurrent = true,
                ExecutionCurrent = false,
                RankExclusionReason = null
            }
        };

    internal static InstalledModelSnapshot Installed()
    {
        var revision = V1('a');
        return new InstalledModelSnapshot("judge.gguf",
            revision,
            [],
            revision,
            [
                new InstalledModelPhysicalMember("judge.gguf",
                    InstalledModelPhysicalMemberRole.Weight,
                    12,
                    new string('b', 64),
                    $"sha256:{new string('b', 64)}:12",
                    ["judge.gguf"],
                    true,
                    null)
            ],
            revision,
            LocalModelOrigin.Imported,
            "llamacpp",
            "map-revision",
            "repo/judge",
            "revision",
            "Q4_K_M",
            GgufRole.Chat,
            V1('c'));
    }

    internal static BenchmarkRuntimeSnapshotV1 Snapshot(InstalledModelSnapshot installed)
    {
        var model = new BenchmarkInstalledModelSnapshotV1(installed.ModelName,
            installed.RegistryRevision,
            [],
            installed.RegistryAliasSetHash,
            installed.Members.Select(static member => new BenchmarkPhysicalMemberSnapshotV1(member.RelativePath,
                         member.Role,
                         member.SizeBytes,
                         member.Sha256,
                         member.OwningAliases,
                         member.Required,
                         member.MetadataSchemaVersion,
                         member.MemberFingerprint))
                     .ToArray(),
            installed.PhysicalMemberSetHash,
            installed.Origin,
            installed.ProviderName!,
            installed.ProviderMappingRevision,
            installed.RepoId,
            installed.SourceRevision,
            installed.ModelName,
            installed.Quantization,
            "chat",
            installed.ModelContentFingerprint);
        return new BenchmarkRuntimeSnapshotV1(1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "task",
            8192,
            new ResolvedAgentRuntime("prompt", [], null, null, 1, AgentName: "Agent"),
            Runtime(8192),
            BenchmarkFrozenPolicies.DeterministicSampling(),
            model,
            new BenchmarkFreezeDependencySetV1("a", "b", "c", "d", "e", "f"),
            "test",
            1,
            "hash");
    }

    internal static string V1(char value) =>
        $"v1:{new string(value, 64)}";

    internal static BenchmarkLlamaRuntimeSnapshotV1 Runtime(int contextTokens, string? kvCacheType = null) =>
        new(GpuVariant.Cpu, contextTokens, null, null, null, kvCacheType, kvCacheType, kvCacheType is not null,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);

    internal static IGpuVariantSelector FixedVariantSelector()
    {
        var selector = Substitute.For<IGpuVariantSelector>();
        selector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(GpuVariant.Cpu);
        return selector;
    }

    internal static ILlamaServerProcessSupervisor PassthroughSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.RunExclusiveBenchmarkAsync(Arg.Any<string>(),
                      Arg.Any<ModelRole>(),
                      Arg.Any<ResolvedLaunchArguments>(),
                      Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
                      Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
                      Arg.Any<CancellationToken>())
                  .Returns(call =>
                  {
                      var modelName = call.ArgAt<string>(0);
                      var context = new LlamaServerProfilingContext(new LlamaServerEndpoint
                      {
                          ModelName = modelName,
                          Role = ModelRole.Chat,
                          BaseAddress = new Uri("http://127.0.0.1:19001")
                      }, []);
                      return call.ArgAt<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(4)(context, call.ArgAt<CancellationToken>(5));
                  });
        return supervisor;
    }

    internal static ILlamaServerEndpointBinding EndpointBinding()
    {
        var binding = Substitute.For<ILlamaServerEndpointBinding>();
        binding.Bind(Arg.Any<LlamaServerEndpoint>()).Returns(Substitute.For<IDisposable>());
        return binding;
    }

    internal sealed class FixedSnapshotFactory : IBenchmarkRuntimeSnapshotFactory
    {
        private readonly BenchmarkRuntimeSnapshotV1 _snapshot;

        public FixedSnapshotFactory(BenchmarkRuntimeSnapshotV1 snapshot)
        {
            _snapshot = snapshot;
        }

        public BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input) =>
            throw new NotSupportedException();

        public byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot) =>
            throw new NotSupportedException();

        public BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload) =>
            _snapshot;
    }

    internal sealed class FixedLeaseProvider : IBenchmarkInstalledModelLeaseProvider
    {
        private readonly FakeLease _lease;

        public FixedLeaseProvider(FakeLease lease)
        {
            _lease = lease;
        }

        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken) =>
            Task.FromResult<IBenchmarkInstalledModelLease>(_lease);
    }

    internal sealed class FakeLease : IBenchmarkInstalledModelLease
    {
        public FakeLease(InstalledModelSnapshot snapshot)
        {
            ModelSnapshot = snapshot;
        }

        public InstalledModelSnapshot ModelSnapshot { get; }
        InstalledModelSnapshot IBenchmarkInstalledModelLease.Snapshot => ModelSnapshot;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     Answers each decision with the next verdict, then repeats the last one forever — so a single verdict is a
    ///     constant answer and a sequence models capacity that frees up (or never does) while the phase waits.
    /// </summary>
    internal sealed class JudgeCapacityService : ICapacityService
    {
        private readonly Queue<CapacityVerdict> _verdicts;

        public JudgeCapacityService(params CapacityVerdict[] verdicts)
        {
            _verdicts = new(verdicts);
        }

        public CapacityRequest? LastRequest { get; private set; }
        public int DecisionCount { get; private set; }
        public string Reason { get; init; } = "capacity";
        public TrackingDisposable Reservation { get; } = new();

        public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct) =>
            Task.FromResult(new CapacityDecision
            {
                Verdict = Next(),
                Reason = "capacity"
            });

        public Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
        {
            LastRequest = request;
            var verdict = Next();
            return Task.FromResult(new CapacityDecision
            {
                Verdict = verdict,
                Reason = Reason,
                Reservation = verdict == CapacityVerdict.Allow ? Reservation : null
            });
        }

        private CapacityVerdict Next()
        {
            DecisionCount++;
            return _verdicts.Count > 1 ? _verdicts.Dequeue() : _verdicts.Peek();
        }
    }

    internal sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() =>
            Disposed = true;
    }

    internal sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
