namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkJudgeExecutorTests
{
    [Test]
    public async Task Execute_SuccessUsesFrozenJudgeContextPersistsStrictResultAndDisposesOwnership()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkJudgeStatus.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkJudgeSuccessCommand? command = null;
        store.MarkJudgeSucceededAsync(Arg.Do<BenchmarkJudgeSuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 JudgeStatus = BenchmarkJudgeStatus.Succeeded,
                 LastStreamSequence = call.Arg<BenchmarkJudgeSuccessCommand>().LastStreamSequence,
                 Version = 5
             });
        var capacity = new JudgeCapacityService(CapacityVerdict.Allow);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        RuntimePackage? assignedPackage = null;
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Do<RuntimePackage>(value => assignedPackage = value), Arg.Any<CancellationToken>())
                  .Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 4096,
                      EffectiveContextTokens = 4096,
                      ModelId = "judge.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId,
                          "{\"schemaVersion\":1,\"score\":5,\"rationale\":\"excellent\"}")));
              });
        await using var lease = new FakeLease(installed);
        var supervisor = PassthroughSupervisor();
        var executor = Executor(store, snapshot, lease, capacity, dispatcher, runner, supervisor);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        AssertEx.Equal<int?>(4096, AssertEx.NotNull(capacity.LastRequest).RequiredContextTokens);
        AssertEx.True(persisted.LastStreamSequence > run.LastStreamSequence);
        AssertEx.Contains(Encoding.UTF8.GetString(persisted.JudgeResultJson.Span), "excellent");
        AssertEx.True(assignment.Disposed);
        AssertEx.True(capacity.Reservation.Disposed);
        AssertEx.True(lease.Disposed);
        var package = AssertEx.NotNull(assignedPackage);
        AssertEx.Equal(BenchmarkFrozenPolicies.JudgeSystemPrompt, package.ResolvedSystemPrompt);
        using (var promptPayload = JsonDocument.Parse(package.ConversationContext[0].Content))
        {
            AssertEx.Equal(BenchmarkFrozenPolicies.JudgeOutputSchemaJson,
                promptPayload.RootElement.GetProperty("outputSchema").GetString());
        }

        // The judge's reply is parsed strictly, so the decode is CONSTRAINED to the same shape rather than merely asked
        // for in prose — one judge invocation in three failed "invalid result" against a 3B model without this.
        var responseSchema = package.ResponseJsonSchema ?? throw new AssertionException("Expected the judge package to carry a response schema.");
        AssertEx.Equal(BenchmarkFrozenPolicies.JudgeResponseFormatSchemaJson,
            JsonSerializer.Serialize(responseSchema),
            "The judge turn must carry the bound-free response-format schema, not the frozen bounded one.");

        AssertEx.Equal("0", AssertEx.NotNull(package.SamplingOptions).Seed);
        AssertEx.Equal(expected: 300, package.Timeouts.InvocationTimeoutSeconds);
        AssertEx.Equal(expected: 30, package.Timeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(expected: 60, package.Timeouts.StreamIdleTimeoutSeconds);
        _ = supervisor.Received(1).RunExclusiveBenchmarkAsync(Arg.Is<string>("judge.gguf"),
            ModelRole.Chat,
            Arg.Is<ResolvedLaunchArguments>(arguments => !arguments.ExploreMode && arguments.CtxSize == 4096),
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenCapacityRejects_FailsOnlyJudgeWithoutDispatcherOrGeneration()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkJudgeStatus.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkRunRecord? failed = null;
        store.MarkJudgeFailedAsync(run.Id,
                 2,
                 Arg.Any<string>(),
                 Arg.Any<long>(),
                 Arg.Any<CancellationToken>())
             .Returns(call => failed = run with
             {
                 JudgeStatus = BenchmarkJudgeStatus.Failed,
                 JudgeErrorMessage = call.ArgAt<string>(2),
                 LastStreamSequence = call.ArgAt<long>(3),
                 Version = 5
             });
        var capacity = new JudgeCapacityService(CapacityVerdict.RejectInsufficient);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, capacity, dispatcher, runner);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run), CancellationToken.None);

        var terminal = AssertEx.NotNull(failed);
        AssertEx.Equal(BenchmarkPrimaryStatus.Succeeded, terminal.PrimaryStatus);
        AssertEx.Equal(BenchmarkJudgeStatus.Failed, terminal.JudgeStatus);
        AssertEx.True(terminal.LastStreamSequence > run.LastStreamSequence);
        _ = dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationAssignedAsync(default!, default);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        AssertEx.True(lease.Disposed);
    }

    [Test]
    [Arguments("q8_0", "KV cache q8_0")]
    [Arguments(null, "KV cache f16")]
    public async Task Execute_JudgeAdmissionSizesAgainstItsFrozenKvCacheTypeAndLogsWhatItDecidedOn(string? frozenKvCacheType, string loggedKvCacheType)
    {
        // The judge books its OWN frozen KV term, and the decision is legible afterwards. Auto/f16 must still reach
        // capacity as the unchanged default (null), while the log names the effective type rather than an empty field.
        var installed = Installed();
        var snapshot = Snapshot(installed, frozenKvCacheType);
        var run = Run(snapshot, BenchmarkJudgeStatus.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkJudgeFailedAsync(run.Id, 2, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 JudgeStatus = BenchmarkJudgeStatus.Failed,
                 Version = 5
             });
        var capacity = new JudgeCapacityService(CapacityVerdict.RejectInsufficient);
        var logger = new RecordingLogger<BenchmarkJudgeExecutor>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            snapshot,
            lease,
            capacity,
            Substitute.For<IWorkerEventDispatcher>(),
            Substitute.For<IInvocationRunner>(),
            logger: logger);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run), CancellationToken.None);

        AssertEx.Equal<string?>(frozenKvCacheType, AssertEx.NotNull(capacity.LastRequest).KvCacheType);
        var admission = AssertEx.NotNull(logger.Entries.FirstOrDefault(entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("capacity admission", StringComparison.Ordinal)));
        foreach (var expected in new[]
                 {
                     run.Id.ToString(), "phase judge", "requested context 4096",
                     "frozen runtime context 4096", loggedKvCacheType, "RejectInsufficient"
                 })
        {
            AssertEx.Contains(admission.Message, expected);
        }
    }

    [Test]
    public async Task Execute_WhenOwnedCancellationWins_UsesWorkItemVersionAndIsIdempotent()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkJudgeStatus.Running, version: 7);
        const int workVersion = 2;
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkJudgeCancelledAsync(run.Id, workVersion, Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 JudgeStatus = BenchmarkJudgeStatus.Cancelled,
                 Version = run.Version + 1
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var cancellations = new BenchmarkCancellationRegistry();
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            AssertEx.True(cancellations.TryCancel(run.Id, BenchmarkWorkKind.Judge));
            return Task.FromCanceled(call.ArgAt<CancellationToken>(1));
        });
        await using var lease = new FakeLease(installed);
        var executor = new BenchmarkJudgeExecutor(store,
            new FixedSnapshotFactory(snapshot),
            new FixedLeaseProvider(lease),
            new JudgeCapacityService(CapacityVerdict.Allow),
            new LocalChatRuntimePackageBuilder(),
            dispatcher,
            runner,
            PassthroughSupervisor(),
            FixedVariantSelector(),
            EndpointBinding(),
            new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions())),
            cancellations,
            new StubEnvironmentFacts(),
            NullLogger<BenchmarkJudgeExecutor>.Instance);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, workVersion, run), CancellationToken.None);

        _ = store.Received(1).MarkJudgeCancelledAsync(run.Id, workVersion, Arg.Any<long>(), Arg.Any<CancellationToken>());
        AssertEx.False(cancellations.TryCancel(run.Id, BenchmarkWorkKind.Judge));
    }

    [Test]
    public async Task Execute_CheckpointsTheJudgeLaunchKeyedByTheClaimedWorkItemBeforeInference()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkJudgeStatus.Running, version: 4) with
        {
            JudgeLaunchIntent = new BenchmarkRunLaunchIntent("cpu", BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto,
                "cpu-variant", LlamaServerLaunchProjection.FlashAttentionAuto, "intended", null)
        };
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkLaunchReceiptCommand? checkpoint = null;
        store.MarkJudgeLaunchReadyAsync(run.Id, 2, 2, Arg.Do<BenchmarkLaunchReceiptCommand>(command => checkpoint = command), Arg.Any<CancellationToken>())
             .Returns(true);
        store.MarkJudgeSucceededAsync(Arg.Any<BenchmarkJudgeSuccessCommand>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 JudgeStatus = BenchmarkJudgeStatus.Succeeded,
                 Version = 5
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        var checkpointedBeforeInference = false;
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  checkpointedBeforeInference = checkpoint is not null;
                  var invocationId = call.Arg<InvocationExecutionContext>().Package.InvocationId;
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, "{\"schemaVersion\":1,\"score\":4,\"rationale\":\"fine\"}")));
                  return Task.CompletedTask;
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, new JudgeCapacityService(CapacityVerdict.Allow), dispatcher, runner);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run), CancellationToken.None);

        AssertEx.True(checkpointedBeforeInference, "The judge launch evidence must be durable before the first token is generated.");
        AssertEx.Equal(BenchmarkKvCacheType.SourceAuto, AssertEx.NotNull(checkpoint).KvCacheTypeSource);
        AssertEx.NotNullOrEmpty(checkpoint!.EnvironmentFactsHash);
        _ = store.Received(1).MarkJudgeLaunchReadyAsync(run.Id, 2, 2, Arg.Any<BenchmarkLaunchReceiptCommand>(), Arg.Any<CancellationToken>());
    }

    private static BenchmarkJudgeExecutor Executor(IBenchmarkStore store,
        BenchmarkRuntimeSnapshotV1 snapshot,
        FakeLease lease,
        ICapacityService capacity,
        IWorkerEventDispatcher dispatcher,
        IInvocationRunner runner,
        ILlamaServerProcessSupervisor? supervisor = null,
        IBenchmarkCancellationRegistry? cancellations = null,
        ILogger<BenchmarkJudgeExecutor>? logger = null) =>
        new(store,
            new FixedSnapshotFactory(snapshot),
            new FixedLeaseProvider(lease),
            capacity,
            new LocalChatRuntimePackageBuilder(),
            dispatcher,
            runner,
            supervisor ?? PassthroughSupervisor(),
            FixedVariantSelector(),
            EndpointBinding(),
            new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions())),
            cancellations ?? new BenchmarkCancellationRegistry(),
            new StubEnvironmentFacts(),
            logger ?? NullLogger<BenchmarkJudgeExecutor>.Instance);

    private sealed class StubEnvironmentFacts : IRuntimeEnvironmentFactsProvider
    {
        public Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct) =>
            Task.FromResult(new RuntimeEnvironmentFactsV1(1, null, null, null, 42, ["hardware"]));
    }

    private static InvocationState State(Guid invocationId, string content) =>
        new()
        {
            InvocationId = invocationId,
            ConversationId = Guid.NewGuid(),
            Status = InvocationStatus.Completed,
            StreamedContent = content,
            StartedAt = DateTimeOffset.UnixEpoch,
            LastUpdatedAt = DateTimeOffset.UnixEpoch
        };

    private static BenchmarkRunRecord Run(BenchmarkRuntimeSnapshotV1 snapshot, BenchmarkJudgeStatus judgeStatus, long version) =>
        new(Guid.NewGuid(),
            snapshot.ProjectId,
            new byte[]
            {
                1
            },
            snapshot.PrimaryModel.ModelName,
            snapshot.PrimaryModel.Origin,
            snapshot.PrimaryModel.ModelContentFingerprint,
            "Agent",
            1,
            snapshot.RequestedContextTokens,
            BenchmarkPrimaryStatus.Succeeded,
            snapshot.RequestedContextTokens,
            10,
            5,
            500,
            BenchmarkExecutionSerialization.SerializeParts([new BenchmarkOutputPart("output", Content: "answer")]),
            1,
            null,
            judgeStatus,
            null,
            null,
            null,
            version,
            1,
            1,
            1,
            1,
            null,
            1);

    private static InstalledModelSnapshot Installed()
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

    private static BenchmarkRuntimeSnapshotV1 Snapshot(InstalledModelSnapshot installed, string? judgeKvCacheType = null)
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
            new BenchmarkJudgeSnapshotV1(true,
                model,
                1,
                1,
                4096,
                BenchmarkFrozenPolicies.JudgeSystemPrompt,
                BenchmarkFrozenPolicies.JudgeOutputSchemaJson,
                Runtime(4096, judgeKvCacheType),
                BenchmarkFrozenPolicies.DeterministicSampling(),
                "judge-hash"),
            new BenchmarkFreezeDependencySetV1("a", "b", "c", "d", "e", "f"),
            "test",
            1,
            "hash");
    }

    private static string V1(char value) =>
        $"v1:{new string(value, 64)}";

    private static BenchmarkLlamaRuntimeSnapshotV1 Runtime(int contextTokens, string? kvCacheType = null) =>
        new(GpuVariant.Cpu, contextTokens, null, null, null, kvCacheType, kvCacheType, kvCacheType is not null,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);

    private static IGpuVariantSelector FixedVariantSelector()
    {
        var selector = Substitute.For<IGpuVariantSelector>();
        selector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(GpuVariant.Cpu);
        return selector;
    }

    private static ILlamaServerProcessSupervisor PassthroughSupervisor()
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
                      var context = new LlamaServerProfilingContext(new LlamaServerEndpoint(modelName, ModelRole.Chat, new Uri("http://127.0.0.1:19001")), []);
                      return call.ArgAt<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(4)(context, call.ArgAt<CancellationToken>(5));
                  });
        return supervisor;
    }

    private static ILlamaServerEndpointBinding EndpointBinding()
    {
        var binding = Substitute.For<ILlamaServerEndpointBinding>();
        binding.Bind(Arg.Any<LlamaServerEndpoint>()).Returns(Substitute.For<IDisposable>());
        return binding;
    }

    private sealed class FixedSnapshotFactory(BenchmarkRuntimeSnapshotV1 snapshot) : IBenchmarkRuntimeSnapshotFactory
    {
        public BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input) =>
            throw new NotSupportedException();

        public byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot) =>
            throw new NotSupportedException();

        public BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload) =>
            snapshot;
    }

    private sealed class FixedLeaseProvider(FakeLease lease) : IBenchmarkInstalledModelLeaseProvider
    {
        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken) =>
            Task.FromResult<IBenchmarkInstalledModelLease>(lease);
    }

    private sealed class FakeLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot ModelSnapshot { get; } = snapshot;
        InstalledModelSnapshot IBenchmarkInstalledModelLease.Snapshot => ModelSnapshot;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class JudgeCapacityService(CapacityVerdict verdict) : ICapacityService
    {
        public CapacityRequest? LastRequest { get; private set; }
        public TrackingDisposable Reservation { get; } = new();

        public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct) =>
            Task.FromResult(new CapacityDecision(verdict, "capacity", false));

        public Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new CapacityDecision(verdict,
                "capacity",
                false,
                verdict == CapacityVerdict.Allow ? Reservation : null));
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() =>
            Disposed = true;
    }

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
