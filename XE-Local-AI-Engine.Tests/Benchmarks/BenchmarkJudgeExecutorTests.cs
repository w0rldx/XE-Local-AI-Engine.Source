namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models.Enums;
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
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<XE_Local_AI_Engine.Client.Models.RuntimePackage>(), Arg.Any<CancellationToken>())
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
        var executor = Executor(store, snapshot, lease, capacity, dispatcher, runner);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        AssertEx.Equal<int?>(4096, AssertEx.NotNull(capacity.LastRequest).RequiredContextTokens);
        AssertEx.True(persisted.LastStreamSequence > run.LastStreamSequence);
        AssertEx.Contains(System.Text.Encoding.UTF8.GetString(persisted.JudgeResultJson.Span), "excellent");
        AssertEx.True(assignment.Disposed);
        AssertEx.True(capacity.Reservation.Disposed);
        AssertEx.True(lease.Disposed);
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

    private static BenchmarkJudgeExecutor Executor(IBenchmarkStore store,
        BenchmarkRuntimeSnapshotV1 snapshot,
        FakeLease lease,
        ICapacityService capacity,
        IWorkerEventDispatcher dispatcher,
        IInvocationRunner runner) =>
        new(store,
            new FixedSnapshotFactory(snapshot),
            new FixedLeaseProvider(lease),
            capacity,
            new LocalChatRuntimePackageBuilder(),
            dispatcher,
            runner,
            new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions())),
            new BenchmarkCancellationRegistry(),
            NullLogger<BenchmarkJudgeExecutor>.Instance);

    private static InvocationState State(Guid invocationId, string content) => new()
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
            new byte[] { 1 },
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
            [new InstalledModelPhysicalMember("judge.gguf",
                InstalledModelPhysicalMemberRole.Weight,
                12,
                new string('b', 64),
                $"sha256:{new string('b', 64)}:12",
                ["judge.gguf"],
                true,
                null)],
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

    private static BenchmarkRuntimeSnapshotV1 Snapshot(InstalledModelSnapshot installed)
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
            model,
            new BenchmarkJudgeSnapshotV1(true, model, 1, 1, 4096, "judge-hash"),
            new BenchmarkFreezeDependencySetV1("a", "b", "c", "d", "e", "f"),
            "test",
            1,
            "hash");
    }

    private static string V1(char value) => $"v1:{new string(value, 64)}";

    private sealed class FixedSnapshotFactory(BenchmarkRuntimeSnapshotV1 snapshot) : IBenchmarkRuntimeSnapshotFactory
    {
        public BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input) => throw new NotSupportedException();
        public byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot) => throw new NotSupportedException();
        public BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload) => snapshot;
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
        public void Dispose() => Disposed = true;
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
