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
///     The launch-identity cutover guard on the pairwise path. A comparison freezes its intended launch identity at enqueue and
///     writes its effective identity at execution, so a scheme change between the two leaves two hashes that were never
///     meant to be compared. The comparison is failed before anything is leased or spawned, which removes the false
///     drift warning at its root instead of rendering it.
/// </summary>
[Category(TestCategories.Unit)]
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
        var work = new BenchmarkClaimedWork { QueueSequence = 3, RunId = Guid.NewGuid(), Kind = BenchmarkWorkKind.Comparison, Attempt = 1, Version = 2, Run = Run(), ComparisonId = ComparisonId };

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
        var work = new BenchmarkClaimedWork { QueueSequence = 3, RunId = Guid.NewGuid(), Kind = BenchmarkWorkKind.Comparison, Attempt = 1, Version = 2, Run = Run(), ComparisonId = ComparisonId };

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
        new()
        {
            Id = ComparisonId,
            ProjectId = Guid.NewGuid(),
            PolicyRevisionId = Guid.NewGuid(),
            CohortGeneration = 1,
            TaskCaseId = null,
            TaskInputHash = string.Empty,
            RunAId = Guid.NewGuid(),
            RunBId = Guid.NewGuid(),
            Order = 0,
            AttemptSequence = 1,
            Sequence = 1,
            Status = BenchmarkJudgeAttemptStatus.Running,
            Verdict = null,
            AnswerATruncated = false,
            AnswerBTruncated = false,
            JudgeExecutionKey = null,
            ErrorMessage = null,
            JudgeRuntimeJson = null,
            EnqueuedAtUtc = 1,
            StartedAtUtc = null,
            CompletedAtUtc = null,
            Version = 1,
            LaunchIntent = new BenchmarkRunLaunchIntent
            {
                Variant = "cpu",
                KvCacheType = "f16",
                KvCacheTypeSource = "auto",
                KvAutoReason = null,
                FlashAttentionMode = LlamaServerLaunchProjection.FlashAttentionAuto,
                IntendedLaunchIdentity = "intended",
                IntendedExecutableSha256 = null,
                LaunchIdentityScheme = launchIdentityScheme
            }
        };

    private static BenchmarkRunRecord Run() =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            RuntimeSnapshotJson = new byte[]
            {
                1
            },
            PrimaryModelName = "model.gguf",
            PrimaryModelOrigin = LocalModelOrigin.Imported,
            ModelContentFingerprint = $"v1:{new string('a', 64)}",
            AgentName = "Agent",
            AgentVersion = 1,
            RequestedContextTokens = 8192,
            PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
            EffectiveContextTokens = null,
            DurationMs = null,
            TotalTokens = null,
            TokensPerSecond = null,
            OutputPartsJson = null,
            LastStreamSequence = 0,
            UserScore = null,
            PrimaryErrorMessage = null,
            Version = 1,
            CreatedAtUtc = 1,
            StartedAtUtc = 1,
            PrimaryCompletedAtUtc = null,
            UpdatedAtUtc = 1
        };
}
