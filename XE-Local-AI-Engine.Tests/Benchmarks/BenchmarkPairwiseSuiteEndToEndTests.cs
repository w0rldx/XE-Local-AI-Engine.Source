namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>R7-02 end to end: the real planner, comparison executor and fitter over a 2-item suite, 27B against 9B.</summary>
/// <remarks>
///     The unit tests of each half passed while the executor still refused every comparison the planner stamped with
///     its task case, so this pins the seam between them.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class BenchmarkPairwiseSuiteEndToEndTests
{
    private static readonly Guid RevisionId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ItemZero = new("cccccccc-0000-0000-0000-000000000000");
    private static readonly Guid ItemOne = new("dddddddd-0000-0000-0000-000000000000");
    private const string ExecutionKey = "exec-v1";
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task TwoItemsByTwoModels_PlanJudgeAndFit_ScoresTheBetterModelAheadOnEachItem()
    {
        var installed = BenchmarkJudgeExecutorTests.Installed();
        var snapshot = BenchmarkJudgeExecutorTests.Snapshot(installed);
        BenchmarkRunRecord[] runs =
        [
            SuiteRun(snapshot, ItemZero, "cell-27b", "big"),
            SuiteRun(snapshot, ItemZero, "cell-9b", "small"),
            SuiteRun(snapshot, ItemOne, "cell-27b", "big"),
            SuiteRun(snapshot, ItemOne, "cell-9b", "small")
        ];
        var candidates = runs.Select(static run => new BenchmarkPairwiseCandidate
                             {
                                 RunId = run.Id,
                                 TaskCaseId = run.TaskItemId,
                                 TaskInputHash = run.TaskInputHash!,
                                 ModelName = run.PrimaryModelName
                             })
                             .ToArray();

        var plan = BenchmarkPairwisePlanner.Plan(candidates, BenchmarkPairwisePolicy.MaximumRuns);
        AssertEx.Equal(expected: 2, plan.Slots.Count, "One pair per item, 27B against 9B.");

        // Stamped exactly as EnsureComparisonsAsync stamps them: the slot's case and input hash, both orders.
        var comparisons = Enumerable.Range(0, plan.Slots.Count * 2)
                                    .Select(index => Comparison(plan.Slots[index / 2], index % 2, installed, index + 1))
                                    .ToArray();
        var store = Substitute.For<IBenchmarkStore>();
        store.GetJudgePolicyRevisionAsync(RevisionId, Arg.Any<CancellationToken>()).Returns(Revision());
        foreach (var run in runs)
        {
            store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        }

        foreach (var comparison in comparisons)
        {
            store.GetComparisonAsync(comparison.Id, Arg.Any<CancellationToken>()).Returns(comparison);
        }

        List<string> failures = [];
        store.MarkComparisonFailedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Do<string>(failures.Add), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        Dictionary<long, string> verdicts = [];
        store.MarkComparisonSucceededAsync(Arg.Do<BenchmarkComparisonSuccessCommand>(command => verdicts[command.QueueSequence] = command.Verdict),
                 Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                  .Returns(_ => new BenchmarkJudgeExecutorTests.TrackingAsyncDisposable());
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  // The judge prefers the answer "big", whichever position it was shown in.
                  var package = call.Arg<InvocationExecutionContext>().Package;
                  using var payload = JsonDocument.Parse(package.ConversationContext[0].Content);
                  var verdict = payload.RootElement.GetProperty("answerA").GetRawText().Contains("big", StringComparison.Ordinal) ? "a" : "b";
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(BenchmarkJudgeExecutorTests.State(package.InvocationId,
                          $$"""{"schemaVersion":1,"verdict":"{{verdict}}","rationale":"The bigger answer."}""")));
                  return Task.CompletedTask;
              });
        await using var lease = new BenchmarkJudgeExecutorTests.FakeLease(installed);
        var executor = Executor(store, snapshot, lease, dispatcher, runner);

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
        }

        AssertEx.Empty(failures, "No comparison of a stamped within-item pair is refused.");
        AssertEx.Equal(expected: 4, verdicts.Count);

        var judged = Enumerable.Range(0, plan.Slots.Count * 2)
                               .Select(index => Comparison(plan.Slots[index / 2], index % 2, installed, index + 1, verdicts[index + 1]))
                               .ToArray();
        var fitStore = Substitute.For<IBenchmarkStore>();
        fitStore.GetCurrentJudgePolicyRevisionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Revision());
        fitStore.GetPairwiseCohortAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(new BenchmarkPairwiseCohortState
                {
                    PolicyRevisionId = RevisionId,
                    CohortGeneration = 1,
                    ComparisonSetVersion = 4,
                    ReferenceExecutionKey = ExecutionKey,
                    ProjectVersion = 1,
                    Candidates = candidates,
                    Comparisons = judged
                });
        fitStore.GetActivePairwiseFitAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((BenchmarkPairwiseFitRecord?)null);
        BenchmarkPairwiseFitCommand? fit = null;
        fitStore.PublishPairwiseFitAsync(Arg.Do<BenchmarkPairwiseFitCommand>(command => fit = command), Arg.Any<CancellationToken>()).Returns(true);

        AssertEx.True(await new BenchmarkPairwiseFitter(fitStore, NullLogger<BenchmarkPairwiseFitter>.Instance).TryPublishAsync(snapshot.ProjectId, CancellationToken.None));

        var scores = (JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry[]>(AssertEx.NotNull(fit).ScoresJson, ScoreOptions) ?? [])
            .ToDictionary(static entry => entry.RunId);
        AssertEx.True(scores[runs[0].Id].Score > scores[runs[1].Id].Score, "Item 0: the 27B wins.");
        AssertEx.True(scores[runs[2].Id].Score > scores[runs[3].Id].Score, "Item 1: the 27B wins.");
    }

    /// <summary>One model over two items (repeats or not) has nothing to compare: the refusal is published at once.</summary>
    [Test]
    public async Task OneModelOverTwoItems_PlansNothingAndPublishesTheRefusalWithoutAJudgeCall()
    {
        var snapshot = BenchmarkJudgeExecutorTests.Snapshot(BenchmarkJudgeExecutorTests.Installed());
        BenchmarkRunRecord[] runs =
        [
            SuiteRun(snapshot, ItemZero, "model-9b", "small"),
            SuiteRun(snapshot, ItemOne, "model-9b", "small"),
            SuiteRun(snapshot, ItemZero, "model-9b", "small") with
            {
                CellKey = "repeat-cell"
            }
        ];
        var store = Substitute.For<IBenchmarkStore>();
        store.GetCurrentJudgePolicyRevisionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Revision());
        store.GetPairwiseCohortAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(new BenchmarkPairwiseCohortState
             {
                 PolicyRevisionId = RevisionId,
                 CohortGeneration = 1,
                 ComparisonSetVersion = 0,
                 ReferenceExecutionKey = null,
                 ProjectVersion = 1,
                 Candidates =
                 [
                     .. runs.Select(static run => new BenchmarkPairwiseCandidate
                     {
                         RunId = run.Id,
                         TaskCaseId = run.TaskItemId,
                         TaskInputHash = run.TaskInputHash!,
                         ModelName = run.PrimaryModelName
                     })
                 ],
                 Comparisons = []
             });
        store.GetActivePairwiseFitAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((BenchmarkPairwiseFitRecord?)null);
        BenchmarkPairwiseFitCommand? fit = null;
        store.PublishPairwiseFitAsync(Arg.Do<BenchmarkPairwiseFitCommand>(command => fit = command), Arg.Any<CancellationToken>()).Returns(true);
        var runtimes = Substitute.For<IBenchmarkJudgeRuntimeResolver>();
        var planner = new BenchmarkPairwisePlanner(store,
            runtimes,
            new BenchmarkPairwiseFitter(store, NullLogger<BenchmarkPairwiseFitter>.Instance),
            Substitute.For<IBenchmarkQueueSignal>(),
            NullLogger<BenchmarkPairwisePlanner>.Instance);

        AssertEx.Equal(expected: 0, await planner.EnsurePairsAsync(snapshot.ProjectId, CancellationToken.None));

        await runtimes.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await store.DidNotReceive().EnsureComparisonsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<BenchmarkPairwiseSlot>>(), Arg.Any<ReadOnlyMemory<byte>?>(),
            Arg.Any<BenchmarkRunLaunchIntent?>(), Arg.Any<CancellationToken>());
        var scores = JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry[]>(AssertEx.NotNull(fit, "The live path publishes the refusal row.").ScoresJson, ScoreOptions) ?? [];
        AssertEx.Equal(expected: 3, scores.Length);
        AssertEx.True(scores.All(static entry => entry.Score is null && entry.Reason == BenchmarkRunJudgeStates.ReasonPairwiseInsufficient));
    }

    private static BenchmarkRunRecord SuiteRun(BenchmarkRuntimeSnapshotV1 snapshot, Guid item, string cell, string answer) =>
        BenchmarkJudgeExecutorTests.Run(snapshot, BenchmarkRunJudgeStates.Running, version: 1) with
        {
            TaskItemId = item,
            TaskInputHash = "v1:" + item,
            CellKey = cell,
            PrimaryModelName = cell,
            OutputPartsJson = BenchmarkExecutionSerialization.SerializeParts([new BenchmarkOutputPart("output", Content: answer)])
        };

    private static BenchmarkComparisonRecord Comparison(BenchmarkPairwiseSlot slot, int order, InstalledModelSnapshot installed, int sequence, string? verdict = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            PolicyRevisionId = RevisionId,
            CohortGeneration = 1,
            TaskCaseId = slot.TaskCaseId,
            TaskInputHash = slot.TaskInputHash,
            RunAId = slot.RunAId,
            RunBId = slot.RunBId,
            Order = order,
            AttemptSequence = 1,
            Sequence = sequence,
            Status = verdict is null ? BenchmarkJudgeAttemptStatus.Running : BenchmarkJudgeAttemptStatus.Succeeded,
            Verdict = verdict,
            AnswerATruncated = false,
            AnswerBTruncated = false,
            JudgeExecutionKey = verdict is null ? null : ExecutionKey,
            ErrorMessage = null,
            JudgeRuntimeJson = BenchmarkJudgeSerialization.SerializeRuntime(new BenchmarkJudgeRuntimeV1(BenchmarkJudgeRuntimeV1.CurrentSchemaVersion,
                BenchmarkInstalledModelSnapshotMapper.ToSnapshot(installed),
                4096,
                BenchmarkJudgeExecutorTests.Runtime(4096),
                BenchmarkFrozenPolicies.DeterministicSampling())),
            EnqueuedAtUtc = 1,
            StartedAtUtc = null,
            CompletedAtUtc = null,
            Version = 1,
            LaunchIntent = new BenchmarkRunLaunchIntent
            {
                Variant = "cpu",
                KvCacheType = BenchmarkKvCacheType.F16,
                KvCacheTypeSource = BenchmarkKvCacheType.SourceAuto,
                KvAutoReason = "cpu-variant",
                FlashAttentionMode = LlamaServerLaunchProjection.FlashAttentionAuto,
                IntendedLaunchIdentity = "intended",
                IntendedExecutableSha256 = null,
                LaunchIdentityScheme = LlamaServerLaunchProjection.IdentitySchemeVersion
            }
        };

    private static BenchmarkJudgePolicyRevisionRecord Revision() =>
        new()
        {
            Id = RevisionId,
            ProjectId = Guid.NewGuid(),
            Revision = 1,
            PolicyJson = BenchmarkJudgeSerialization.SerializePolicy(BenchmarkJudgeExecutorTests.Policy() with
            {
                Mode = BenchmarkJudgePolicyModes.Pairwise
            }),
            PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ReferenceExecutionKey = ExecutionKey,
            CohortGeneration = 1,
            CreatedAtUtc = 1
        };

    private static BenchmarkComparisonExecutor Executor(IBenchmarkStore store,
        BenchmarkRuntimeSnapshotV1 snapshot,
        BenchmarkJudgeExecutorTests.FakeLease lease,
        IWorkerEventDispatcher dispatcher,
        IInvocationRunner runner) =>
        new(store,
            new BenchmarkJudgeExecutorTests.FixedSnapshotFactory(snapshot),
            new BenchmarkJudgeExecutorTests.FixedLeaseProvider(lease),
            new BenchmarkJudgeExecutorTests.JudgeCapacityService(CapacityVerdict.Allow),
            new LocalChatRuntimePackageBuilder(),
            dispatcher,
            runner,
            BenchmarkJudgeExecutorTests.PassthroughSupervisor(),
            BenchmarkJudgeExecutorTests.FixedVariantSelector(),
            BenchmarkJudgeExecutorTests.EndpointBinding(),
            new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions())),
            new BenchmarkCancellationRegistry(),
            new BenchmarkJudgeExecutorTests.StubEnvironmentFacts(),
            Substitute.For<IBenchmarkPairwiseFitter>(),
            new BenchmarkAdmissionRetry
            {
                MaxRetries = 0,
                Interval = TimeSpan.Zero
            },
            NullLogger<BenchmarkComparisonExecutor>.Instance);
}
