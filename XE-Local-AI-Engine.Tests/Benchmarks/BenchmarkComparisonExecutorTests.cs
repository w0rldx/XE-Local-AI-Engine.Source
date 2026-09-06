namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The D14 cutover guard on the pairwise path. A comparison freezes its intended launch identity at enqueue and
///     writes its effective identity at execution, so a scheme change between the two leaves two hashes that were never
///     meant to be compared. The comparison is failed before anything is leased or spawned, which removes the false
///     drift warning at its root instead of rendering it.
/// </summary>
public sealed class BenchmarkComparisonExecutorTests
{
    private static readonly Guid ComparisonId = new("55555555-5555-5555-5555-555555555555");

    [Test]
    public async Task Execute_ForAComparisonFrozenUnderAnOlderIdentityScheme_FailsWithTheSupersededReason()
    {
        var store = Substitute.For<IBenchmarkStore>();
        store.GetComparisonAsync(ComparisonId, Arg.Any<CancellationToken>())
             .Returns(Comparison(launchIdentityScheme: null));
        string? failureMessage = null;
        store.MarkComparisonFailedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Do<string>(message => failureMessage = message), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        var runner = Substitute.For<IInvocationRunner>();
        var work = new BenchmarkClaimedWork(3, Guid.NewGuid(), BenchmarkWorkKind.Comparison, 1, 2, Run(), ComparisonId: ComparisonId);

        await Executor(store, runner).ExecuteAsync(work, CancellationToken.None);

        AssertEx.Contains(AssertEx.NotNull(failureMessage), BenchmarkLaunchIdentityScheme.SupersededReason);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        await store.DidNotReceive().GetJudgePolicyRevisionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_ForAComparisonFrozenUnderTheCurrentScheme_PassesTheGuard()
    {
        // The guard is a no-op for current work: execution continues and fails later, on the policy revision it then
        // cannot find — which is exactly the reach the guard must not shorten.
        var store = Substitute.For<IBenchmarkStore>();
        store.GetComparisonAsync(ComparisonId, Arg.Any<CancellationToken>()).Returns(Comparison());
        string? failureMessage = null;
        store.MarkComparisonFailedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Do<string>(message => failureMessage = message), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        var work = new BenchmarkClaimedWork(3, Guid.NewGuid(), BenchmarkWorkKind.Comparison, 1, 2, Run(), ComparisonId: ComparisonId);

        await Executor(store, Substitute.For<IInvocationRunner>()).ExecuteAsync(work, CancellationToken.None);

        AssertEx.False(AssertEx.NotNull(failureMessage).Contains(BenchmarkLaunchIdentityScheme.SupersededReason, StringComparison.Ordinal),
            "a comparison frozen under the current scheme must reach the rest of the executor untouched.");
        await store.Received(1).GetJudgePolicyRevisionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static BenchmarkComparisonExecutor Executor(IBenchmarkStore store, IInvocationRunner runner) =>
        new(store,
            Substitute.For<IBenchmarkRuntimeSnapshotFactory>(),
            Substitute.For<IBenchmarkInstalledModelLeaseProvider>(),
            Substitute.For<ICapacityService>(),
            Substitute.For<ILocalChatRuntimePackageBuilder>(),
            Substitute.For<IWorkerEventDispatcher>(),
            runner,
            Substitute.For<ILlamaServerProcessSupervisor>(),
            Substitute.For<IGpuVariantSelector>(),
            Substitute.For<ILlamaServerEndpointBinding>(),
            Substitute.For<IBenchmarkEventBuffer>(),
            new BenchmarkCancellationRegistry(),
            Substitute.For<IRuntimeEnvironmentFactsProvider>(),
            Substitute.For<IBenchmarkPairwiseFitter>(),
            new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero),
            NullLogger<BenchmarkComparisonExecutor>.Instance);

    private static BenchmarkComparisonRecord Comparison(int? launchIdentityScheme = LlamaServerLaunchProjection.IdentitySchemeVersion) =>
        new(ComparisonId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            CohortGeneration: 1,
            TaskCaseId: null,
            TaskInputHash: string.Empty,
            RunAId: Guid.NewGuid(),
            RunBId: Guid.NewGuid(),
            Order: 0,
            AttemptSequence: 1,
            Sequence: 1,
            BenchmarkJudgeAttemptStatus.Running,
            Verdict: null,
            AnswerATruncated: false,
            AnswerBTruncated: false,
            JudgeExecutionKey: null,
            ErrorMessage: null,
            JudgeRuntimeJson: null,
            EnqueuedAtUtc: 1,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            Version: 1,
            new BenchmarkRunLaunchIntent("cpu", "f16", "auto", null, LlamaServerLaunchProjection.FlashAttentionAuto,
                "intended", null, launchIdentityScheme));

    private static BenchmarkRunRecord Run() =>
        new(Guid.NewGuid(), Guid.NewGuid(), new byte[]
            {
                1
            }, "model.gguf", LocalModelOrigin.Imported, $"v1:{new string('a', 64)}", "Agent", 1, 8192,
            BenchmarkPrimaryStatus.Succeeded, null, null, null, null, null, 0, null, null, 1, 1, 1, null, 1);
}
