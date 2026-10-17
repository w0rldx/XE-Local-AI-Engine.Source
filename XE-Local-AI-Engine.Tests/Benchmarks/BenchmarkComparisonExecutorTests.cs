namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
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
    private static readonly Guid ItemZero = new("cccccccc-0000-0000-0000-000000000000");
    private static readonly Guid ItemOne = new("dddddddd-0000-0000-0000-000000000000");

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
        var work = new BenchmarkClaimedWork
        {
            QueueSequence = 3,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkWorkKind.Comparison,
            Attempt = 1,
            Version = 2,
            Run = Run(),
            ComparisonId = ComparisonId
        };

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
        var work = new BenchmarkClaimedWork
        {
            QueueSequence = 3,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkWorkKind.Comparison,
            Attempt = 1,
            Version = 2,
            Run = Run(),
            ComparisonId = ComparisonId
        };

        await Executor(store, Substitute.For<IInvocationRunner>()).ExecuteAsync(work, CancellationToken.None);

        AssertEx.False(AssertEx.NotNull(failureMessage).Contains(BenchmarkLaunchIdentityScheme.SupersededReason, StringComparison.Ordinal),
            "a comparison frozen under the current scheme must reach the rest of the executor untouched.");
        await store.Received(1).GetJudgePolicyRevisionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_TwoItemsWithTheirOwnReferences_EachJudgePromptCarriesItsItemsReference()
    {
        var seen = await JudgeOnePairPerItemAsync(policyReference: "policy reference", zeroReference: "reference zero", oneReference: "reference one");

        AssertEx.Equal("reference zero", seen[ItemZero]);
        AssertEx.Equal("reference one", seen[ItemOne]);
    }

    [Test]
    public async Task Execute_AnItemWithoutAReference_FallsBackToThePolicyReference()
    {
        var seen = await JudgeOnePairPerItemAsync(policyReference: "policy reference", zeroReference: "reference zero", oneReference: null);

        AssertEx.Equal("reference zero", seen[ItemZero]);
        AssertEx.Equal("policy reference", seen[ItemOne]);
    }

    /// <summary>Judges one comparison per item of a two-item, two-model suite and returns the reference each judge prompt carried, by item.</summary>
    private static async Task<Dictionary<Guid, string?>> JudgeOnePairPerItemAsync(string? policyReference, string? zeroReference, string? oneReference)
    {
        var installed = BenchmarkJudgeExecutorTests.Installed();
        var snapshot = BenchmarkJudgeExecutorTests.Snapshot(installed);
        BenchmarkRunRecord[] runs =
        [
            BenchmarkPairwiseSuiteEndToEndTests.SuiteRun(snapshot, ItemZero, "cell-27b", "big"),
            BenchmarkPairwiseSuiteEndToEndTests.SuiteRun(snapshot, ItemZero, "cell-9b", "small"),
            BenchmarkPairwiseSuiteEndToEndTests.SuiteRun(snapshot, ItemOne, "cell-27b", "big"),
            BenchmarkPairwiseSuiteEndToEndTests.SuiteRun(snapshot, ItemOne, "cell-9b", "small")
        ];
        var plan = BenchmarkPairwisePlanner.Plan([
            .. runs.Select(static run => new BenchmarkPairwiseCandidate
            {
                RunId = run.Id,
                TaskCaseId = run.TaskItemId,
                TaskInputHash = run.TaskInputHash!,
                ModelName = run.PrimaryModelName
            })
        ], BenchmarkPairwisePolicy.MaximumRuns);
        var comparisons = plan.Slots.Select((slot, index) => BenchmarkPairwiseSuiteEndToEndTests.Comparison(slot, order: 0, installed, index + 1)).ToArray();

        var store = Substitute.For<IBenchmarkStore>();
        store.GetJudgePolicyRevisionAsync(BenchmarkPairwiseSuiteEndToEndTests.RevisionId, Arg.Any<CancellationToken>())
             .Returns(BenchmarkPairwiseSuiteEndToEndTests.Revision(policyReference));
        store.ListTaskItemsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([Item(ItemZero, 0, zeroReference), Item(ItemOne, 1, oneReference)]);
        foreach (var run in runs)
        {
            store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        }

        foreach (var comparison in comparisons)
        {
            store.GetComparisonAsync(comparison.Id, Arg.Any<CancellationToken>()).Returns(comparison);
        }

        List<string> failures = [];
        store.MarkComparisonFailedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Do<string>(failures.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                  .Returns(_ => new BenchmarkJudgeExecutorTests.TrackingAsyncDisposable());
        var references = new List<string?>();
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  var package = call.Arg<InvocationExecutionContext>().Package;
                  using var payload = JsonDocument.Parse(package.ConversationContext[0].Content);
                  var reference = payload.RootElement.GetProperty("referenceAnswer");
                  references.Add(reference.ValueKind == JsonValueKind.Null ? null : reference.GetString());
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(BenchmarkJudgeExecutorTests.State(package.InvocationId,
                          """{"schemaVersion":1,"verdict":"a","rationale":"A."}""")));
                  return Task.CompletedTask;
              });
        await using var lease = new BenchmarkJudgeExecutorTests.FakeLease(installed);
        var executor = BenchmarkPairwiseSuiteEndToEndTests.Executor(store, snapshot, lease, dispatcher, runner);

        Dictionary<Guid, string?> seen = [];
        foreach (var comparison in comparisons)
        {
            var runA = runs.Single(run => run.Id == comparison.RunAId);
            await executor.ExecuteAsync(new BenchmarkClaimedWork
            {
                QueueSequence = comparison.Sequence,
                RunId = runA.Id,
                Kind = BenchmarkWorkKind.Comparison,
                Attempt = 1,
                Version = 1,
                Run = runA,
                ComparisonId = comparison.Id
            }, CancellationToken.None);
            seen[runA.TaskItemId!.Value] = references[^1];
        }

        AssertEx.Empty(failures, "Every comparison reaches its judge.");
        AssertEx.Equal(expected: 2, references.Count);
        return seen;
    }

    private static BenchmarkTaskItemRecord Item(Guid id, int index, string? referenceAnswer) =>
        new()
        {
            Id = id,
            ProjectId = Guid.NewGuid(),
            ParentItemId = null,
            Index = index,
            Kind = BenchmarkTaskItemKinds.Prompt,
            Revision = 1,
            InputHash = "v1:" + id,
            CountsTowardScore = true,
            PromptJson = JsonSerializer.SerializeToUtf8Bytes("a prompt"),
            ReferenceAnswerJson = referenceAnswer is null ? null : JsonSerializer.SerializeToUtf8Bytes(referenceAnswer),
            VerifierConfigJson = null,
            GeneratorConfigJson = null,
            Version = 1,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0
        };

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
            new BenchmarkAdmissionRetry
            {
                MaxRetries = 0,
                Interval = TimeSpan.Zero
            },
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
