namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkRunExecutorTests
{
    [Test]
    public async Task Execute_WhenExecutionSnapshotFingerprintChanged_FailsBeforeCapacityDispatcherOrGeneration()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var expected = Installed("model.gguf", 'a');
        var actual = Installed("model.gguf", 'b');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkRunRecord? failed = null;
        store.MarkPrimaryFailedAsync(run.Id,
                run.Version,
                Arg.Do<string>(message => AssertEx.Contains(message, "installed model changed")),
                Arg.Do<long>(sequence => AssertEx.True(sequence > 0)),
                Arg.Any<CancellationToken>())
             .Returns(call => failed = run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 PrimaryErrorMessage = call.ArgAt<string>(2),
                 LastStreamSequence = call.ArgAt<long>(3),
                 Version = run.Version + 1
             });
        var snapshot = Snapshot(expected);
        var capacity = new RecordingCapacityService();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(actual);
        var executor = new BenchmarkRunExecutor(store,
            new FixedSnapshotFactory(snapshot),
            new FixedLeaseProvider(lease),
            capacity,
            Substitute.For<ILocalChatRuntimePackageBuilder>(),
            dispatcher,
            runner,
            Buffer(),
            new BenchmarkCancellationRegistry(),
            NullLogger<BenchmarkRunExecutor>.Instance);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.NotNull(failed);
        AssertEx.Equal(0, capacity.DecisionCount);
        _ = dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationAssignedAsync(default!, default);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        AssertEx.True(lease.Disposed);
    }

    [Test]
    public async Task Execute_SuccessUsesFrozenContextPersistsCanonicalPartsAndDisposesOwnedResources()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2) with { JudgeStatus = BenchmarkJudgeStatus.Pending };
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkPrimarySuccessCommand? command = null;
        store.MarkPrimarySucceededAsync(Arg.Do<BenchmarkPrimarySuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 JudgeStatus = BenchmarkJudgeStatus.Queued,
                 LastStreamSequence = call.Arg<BenchmarkPrimarySuccessCommand>().LastStreamSequence,
                 Version = 3
             });
        var capacity = new RecordingCapacityService();
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
                      RequestedContextTokens = 8192,
                      EffectiveContextTokens = 8192,
                      ModelId = "model.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Running, "answer")));
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "answer", 20, 100)));
              });
        await using var lease = new FakeLease(installed);
        var cancellationRegistry = new BenchmarkCancellationRegistry();
        var executor = Executor(store, Snapshot(installed), lease, capacity, dispatcher, runner, cancellationRegistry);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        AssertEx.Equal<int?>(8192, AssertEx.NotNull(capacity.LastRequest).RequiredContextTokens);
        AssertEx.Contains(BenchmarkExecutionSerialization.DeserializeParts(persisted.OutputPartsJson.Span),
            static part => part.Kind == "output" && part.Content == "answer");
        AssertEx.Equal<int?>(20, persisted.TotalTokens);
        AssertEx.Equal<double?>(200d, persisted.TokensPerSecond);
        AssertEx.True(persisted.LastStreamSequence > 0);
        AssertEx.True(assignment.Disposed);
        AssertEx.True(AssertEx.NotNull(capacity.Reservation).Disposed);
        AssertEx.True(lease.Disposed);
        AssertEx.False(cancellationRegistry.TryCancel(run.Id, BenchmarkWorkKind.Primary));
        _ = store.Received(1).MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenRunnerCancels_TerminalizesPrimaryAndDisposesOwnedResources()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkRunRecord? cancelled = null;
        store.MarkPrimaryCancelledAsync(run.Id, run.Version, Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => cancelled = run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Cancelled,
                 LastStreamSequence = call.ArgAt<long>(2),
                 Version = 3
             });
        var capacity = new RecordingCapacityService();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<XE_Local_AI_Engine.Client.Models.RuntimePackage>(), Arg.Any<CancellationToken>())
                  .Returns(assignment);
        var cancellationRegistry = new BenchmarkCancellationRegistry();
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  AssertEx.True(cancellationRegistry.TryCancel(run.Id, BenchmarkWorkKind.Primary));
                  return Task.FromCanceled(call.ArgAt<CancellationToken>(1));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            Snapshot(installed),
            lease,
            capacity,
            dispatcher,
            runner,
            cancellationRegistry);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal(BenchmarkPrimaryStatus.Cancelled, AssertEx.NotNull(cancelled).PrimaryStatus);
        AssertEx.True(cancelled!.LastStreamSequence > 0);
        AssertEx.True(assignment.Disposed);
        AssertEx.True(AssertEx.NotNull(capacity.Reservation).Disposed);
        AssertEx.True(lease.Disposed);
    }

    [Test]
    public async Task Execute_WhenHostStops_LeavesRunningWorkForStartupRecovery()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        var capacity = new RecordingCapacityService();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<XE_Local_AI_Engine.Client.Models.RuntimePackage>(), Arg.Any<CancellationToken>())
                  .Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call => Task.FromCanceled(call.ArgAt<CancellationToken>(1)));
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            Snapshot(installed),
            lease,
            capacity,
            dispatcher,
            runner,
            new BenchmarkCancellationRegistry());
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), stopping.Token));

        _ = store.DidNotReceive().MarkPrimaryCancelledAsync(run.Id, run.Version, Arg.Any<long>(), Arg.Any<CancellationToken>());
        _ = store.DidNotReceive().MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        AssertEx.True(assignment.Disposed);
        AssertEx.True(AssertEx.NotNull(capacity.Reservation).Disposed);
        AssertEx.True(lease.Disposed);
    }

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus status, long version) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new byte[] { 1 }, "model.gguf", LocalModelOrigin.Imported, V1('a'), "Agent", 1, 8192,
            status, null, null, null, null, null, 0, null, BenchmarkJudgeStatus.Disabled, null, null, null, version,
            1, 1, null, null, null, 1);

    private static BenchmarkRunExecutor Executor(IBenchmarkStore store,
        BenchmarkRuntimeSnapshotV1 snapshot,
        FakeLease lease,
        ICapacityService capacity,
        IWorkerEventDispatcher dispatcher,
        IInvocationRunner runner,
        IBenchmarkCancellationRegistry cancellations) =>
        new(store,
            new FixedSnapshotFactory(snapshot),
            new FixedLeaseProvider(lease),
            capacity,
            new LocalChatRuntimePackageBuilder(),
            dispatcher,
            runner,
            Buffer(),
            cancellations,
            NullLogger<BenchmarkRunExecutor>.Instance);

    private static InvocationState State(Guid invocationId,
        InvocationStatus status,
        string content,
        int? totalTokens = null,
        long? durationMs = null) => new()
    {
        InvocationId = invocationId,
        ConversationId = Guid.NewGuid(),
        Status = status,
        StreamedContent = content,
        StartedAt = DateTimeOffset.UnixEpoch,
        LastUpdatedAt = DateTimeOffset.UnixEpoch,
        TotalTokens = totalTokens,
        GenerationDurationMs = durationMs
    };

    private static InstalledModelSnapshot Installed(string name, char fingerprintCharacter)
    {
        var revision = V1('c');
        var fingerprint = V1(fingerprintCharacter);
        return new InstalledModelSnapshot(name,
            revision,
            [],
            revision,
            [new InstalledModelPhysicalMember(name,
                InstalledModelPhysicalMemberRole.Weight,
                12,
                new string('d', 64),
                $"sha256:{new string('d', 64)}:12",
                [name],
                true,
                null)],
            revision,
            LocalModelOrigin.Imported,
            "llamacpp",
            "map-revision",
            "repo/model",
            "revision",
            "Q4_K_M",
            GgufRole.Chat,
            fingerprint);
    }

    private static BenchmarkRuntimeSnapshotV1 Snapshot(InstalledModelSnapshot model)
    {
        var frozen = new BenchmarkInstalledModelSnapshotV1(model.ModelName,
            model.RegistryRevision,
            [],
            model.RegistryAliasSetHash,
            model.Members.Select(static member => new BenchmarkPhysicalMemberSnapshotV1(member.RelativePath,
                    member.Role,
                    member.SizeBytes,
                    member.Sha256,
                    member.OwningAliases,
                    member.Required,
                    member.MetadataSchemaVersion,
                    member.MemberFingerprint))
                .ToArray(),
            model.PhysicalMemberSetHash,
            model.Origin,
            model.ProviderName!,
            model.ProviderMappingRevision,
            model.RepoId,
            model.SourceRevision,
            model.ModelName,
            model.Quantization,
            "chat",
            model.ModelContentFingerprint);
        return new BenchmarkRuntimeSnapshotV1(1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "task",
            8192,
            new ResolvedAgentRuntime("prompt", [], null, null, 1, AgentName: "Agent"),
            frozen,
            new BenchmarkJudgeSnapshotV1(false, null, 1, 1, null, "hash"),
            new BenchmarkFreezeDependencySetV1("a", "b", "c", "d", "e", null),
            "test",
            1,
            "hash");
    }

    private static string V1(char value) => $"v1:{new string(value, 64)}";

    private static BenchmarkEventBuffer Buffer() => new(Options.Create(new BenchmarkEventBufferOptions()));

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

    private sealed class RecordingCapacityService : ICapacityService
    {
        public int DecisionCount { get; private set; }
        public CapacityRequest? LastRequest { get; private set; }
        public TrackingDisposable? Reservation { get; private set; }

        public Task<CapacityDecision> DecideAsync(string modelName, XE_Local_AI_Engine.Providers.LlamaServer.ModelRole role, CancellationToken ct)
        {
            DecisionCount++;
            return Task.FromResult(new CapacityDecision(CapacityVerdict.Allow, "allowed", false));
        }

        public Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
        {
            DecisionCount++;
            LastRequest = request;
            Reservation = new TrackingDisposable();
            return Task.FromResult(new CapacityDecision(CapacityVerdict.Allow, "allowed", false, Reservation));
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
