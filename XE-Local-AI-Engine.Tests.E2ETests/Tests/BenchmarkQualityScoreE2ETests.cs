namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.E2ETests.Common;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     The in-project quality score, driven through the shipped page: the ranked runs table with its cohort line and
///     source chips, the 0..100 operator override and its removal, the judge panel's per-criterion breakdown, and the
///     mandatory re-judge confirmation a judge change on a frozen project must go through.
///     <para>
///         <b>What is real and what is not.</b> Every endpoint, the judge-policy hash, the revision and attempt rows,
///         the ranking SQL and the whole page are production code. What a browser host structurally cannot do is run a
///         model: it boots with <c>RemoveAll&lt;IHostedService&gt;</c> (no benchmark queue worker), its <c>llamacpp</c>
///         provider is a substitute, and no GGUF is installed — so the primary invocation and the judge model call are
///         driven here through the same <see cref="IBenchmarkStore" /> transitions the executors use, with a canned
///         rubric verdict. The two DI seams that make that possible are documented on
///         <see cref="BenchmarkE2ETestDoubles" />. Real models on real hardware are covered by the live 5090 pass, not
///         by this suite.
///     </para>
///     <para>
///         Serial: benchmark projects are node-global, and <c>LocalImportAndBenchmarksE2ETests</c> asserts the
///         node-wide empty state. The project is deleted in <c>[After(Test)]</c>.
///     </para>
/// </summary>
[Category("Page")]
public sealed class BenchmarkQualityScoreE2ETests : XESerialE2ETestBase
{
    private const string ProjectName = "E2E quality score project";
    private const string ReferenceAnswer = "A correct, complete answer that follows the requested format.";
    private const string SecondaryModelName = "e2e-benchmark-model-b.gguf";

    /// <summary>One key for every judging in this test, so all attempts share one rank cohort.</summary>
    private const string ExecutionKey = "e2e-judge-execution-key";

    private static readonly LocatorAssertionsToHaveTextOptions Polled = new()
    {
        // The runs list only polls while a run is active (2 s), and the last transition this test makes is written
        // straight to the store, so the assertion has to outlive one poll plus a render.
        Timeout = 30_000
    };

    /// <summary>Per-criterion verdict each run's judgings are given, so the rank order is a fact about the scores.</summary>
    private readonly Dictionary<Guid, int> _criterionScores = [];

    private Guid _projectId;
    private Guid _runA;
    private Guid _runB;

    [Test]
    public async Task Benchmarks_QualityScore_RanksJudgedRunsAndReJudgesOnAJudgeChange()
    {
        var judgeScoreA = await SeedProjectWithTwoJudgedRunsAsync().ConfigureAwait(false);

        await Page.GotoAsync($"{NodeAppUrl}/benchmarks", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        }).ConfigureAwait(false);
        // The project card, not the detail heading that carries the same text once it is selected.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = ProjectName
        }).First.ClickAsync().ConfigureAwait(false);

        // 1. The cohort ranked both judgings under revision 1 of the judge policy, generation 1.
        await Expect(Page.GetByTestId("benchmark-runs-table")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.GetByTestId("benchmark-rank-cohort"))
              .ToHaveTextAsync("2 of 2 ranked · judge policy r1 · gen 1", Polled)
              .ConfigureAwait(false);
        await ExpectRankAsync(_runA, "1").ConfigureAwait(false);
        await ExpectRankAsync(_runB, "2").ConfigureAwait(false);
        await Expect(QualityScore(_runA)).ToHaveTextAsync(judgeScoreA.ToString(provider: null)).ConfigureAwait(false);
        await Expect(Page.GetByTestId($"benchmark-quality-source-{_runA}")).ToHaveTextAsync("judge").ConfigureAwait(false);

        // 2. The judge panel of the top run: policy chip, weighted score, and one bar per rubric criterion.
        // The page opens exactly ONE detail pane by itself (the newest run), so the run under test is selected here
        // rather than assumed. Check is idempotent, so this is also correct when it already happens to be the one.
        await Page.GetByTestId($"benchmark-run-select-{_runA}").CheckAsync().ConfigureAwait(false);
        var paneA = Page.GetByTestId($"benchmark-run-{_runA}");
        await Expect(paneA.GetByTestId("benchmark-judge-policy")).ToHaveTextAsync("policy r1").ConfigureAwait(false);
        await Expect(paneA.GetByTestId("benchmark-judge-score"))
              .ToHaveTextAsync($"Judge score: {judgeScoreA} / 100")
              .ConfigureAwait(false);
        await Expect(paneA.GetByTestId("benchmark-judge-criteria").GetByRole(AriaRole.Progressbar))
              .ToHaveCountAsync(BenchmarkJudgeRubricDefaults.Default().Criteria.Count)
              .ConfigureAwait(false);

        // 3. An operator score overrides the judge for ranking without hiding it, and 80 still outranks the other run.
        await paneA.GetByTestId("benchmark-score-input").FillAsync("80").ConfigureAwait(false);
        await paneA.GetByTestId("benchmark-score-save").ClickAsync().ConfigureAwait(false);
        await Expect(Page.GetByTestId($"benchmark-quality-source-{_runA}")).ToHaveTextAsync("operator", Polled).ConfigureAwait(false);
        await Expect(QualityScore(_runA)).ToHaveTextAsync("80").ConfigureAwait(false);
        await ExpectRankAsync(_runA, "1").ConfigureAwait(false);
        await Expect(paneA.GetByTestId("benchmark-judge-score"))
              .ToHaveTextAsync($"Judge score: {judgeScoreA} / 100", Polled)
              .ConfigureAwait(false);

        // 4. Clearing the override hands ranking back to the judge score.
        await paneA.GetByTestId("benchmark-score-clear").ClickAsync().ConfigureAwait(false);
        await Expect(Page.GetByTestId($"benchmark-quality-source-{_runA}")).ToHaveTextAsync("judge", Polled).ConfigureAwait(false);
        await Expect(QualityScore(_runA)).ToHaveTextAsync(judgeScoreA.ToString(provider: null)).ConfigureAwait(false);

        // 5. Changing the judge on a FROZEN project is refused until the implied re-judge is confirmed.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Edit judge"
        }).ClickAsync().ConfigureAwait(false);
        await Expect(Page.GetByTestId("benchmark-rubric-editor")).ToBeVisibleAsync().ConfigureAwait(false);
        var firstWeight = Page.GetByTestId("benchmark-rubric-criterion-0").GetByLabel("Weight");
        await firstWeight.FillAsync("50").ConfigureAwait(false);
        await Expect(firstWeight).ToHaveValueAsync("50").ConfigureAwait(false);
        var refusal = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                                    {
                                        Name = "Save judge"
                                    }).ClickAsync().ConfigureAwait(false),
                                    response => response.Request.Method == "PUT"
                                                && response.Url.EndsWith("/judge", StringComparison.Ordinal),
                                    new PageRunAndWaitForResponseOptions
                                    {
                                        Timeout = 15_000
                                    })
                                .ConfigureAwait(false);
        var refusalBody = await refusal.TextAsync().ConfigureAwait(false);
        if (refusal.Status != 409)
        {
            throw new InvalidOperationException($"An unconfirmed judge change must be refused with 409 RejudgeRequired; got {refusal.Status}: {refusalBody}");
        }

        var confirm = Page.GetByTestId("benchmark-rejudge-confirm");
        await Expect(confirm).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(confirm).ToContainTextAsync("All 2 succeeded runs will be re-judged").ConfigureAwait(false);
        await Page.GetByTestId("benchmark-rejudge-confirm-accept").ClickAsync().ConfigureAwait(false);

        // 6. The activation reset the cohort and enqueued one fresh attempt per succeeded run under revision 2.
        await Expect(paneA.GetByTestId("benchmark-judge-state")).ToHaveTextAsync("Judge queued", Polled).ConfigureAwait(false);
        await Expect(paneA.GetByTestId("benchmark-judge-policy")).ToHaveTextAsync("policy r2", Polled).ConfigureAwait(false);

        // 7. Those attempts succeed (store-driven, see the class summary) and the new cohort ranks under r2. The
        // generation stays 1: it counts resets OF A REVISION, and revision 2 is new, so its first cohort is its first.
        var rejudgedScoreA = await CompleteQueuedJudgeWorkAsync().ConfigureAwait(false);
        await Expect(paneA.GetByTestId("benchmark-judge-state")).ToHaveTextAsync("Judged", Polled).ConfigureAwait(false);
        await Expect(Page.GetByTestId("benchmark-rank-cohort"))
              .ToHaveTextAsync("2 of 2 ranked · judge policy r2 · gen 1", Polled)
              .ConfigureAwait(false);
        await Expect(paneA.GetByTestId("benchmark-judge-policy")).ToHaveTextAsync("policy r2").ConfigureAwait(false);
        await Expect(QualityScore(_runA)).ToHaveTextAsync(rejudgedScoreA.ToString(provider: null), Polled).ConfigureAwait(false);
        await ExpectRankAsync(_runA, "1").ConfigureAwait(false);
    }

    [After(Test)]
    public async Task RemoveSeededProjectAsync()
    {
        if (_projectId == Guid.Empty)
        {
            return;
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBenchmarkStore>();

        // A project refuses deletion while any of its work is non-terminal, so a test that failed part way through
        // must not leave the node-wide empty state permanently occupied for the pooled phase.
        while (await store.ClaimNextAsync().ConfigureAwait(false) is { } work)
        {
            if (work.Kind == BenchmarkWorkKind.Judge)
            {
                _ = await store.MarkJudgeCancelledAsync(work.RunId, work.Version).ConfigureAwait(false);
                continue;
            }

            _ = await store.MarkPrimaryCancelledAsync(work.RunId, work.Version).ConfigureAwait(false);
        }

        // A project refuses deletion while it still has runs, so the runs go first — the same order the page's
        // own delete controls impose on the operator.
        foreach (var run in (await store.ListRunsAsync(_projectId, skip: 0, take: 100).ConfigureAwait(false)).Items)
        {
            await store.DeleteRunAsync(run.Id, run.Version).ConfigureAwait(false);
        }

        if (await store.GetProjectAsync(_projectId).ConfigureAwait(false) is { } project)
        {
            await store.DeleteProjectAsync(_projectId, project.Version).ConfigureAwait(false);
        }

        _projectId = Guid.Empty;
    }

    /// <summary>The quality-score cell of one row: the number rendered beside the source chip.</summary>
    private ILocator QualityScore(Guid runId) =>
        Page.GetByTestId($"benchmark-run-row-{runId}").Locator("td").Nth(3).Locator("p").First;

    private Task ExpectRankAsync(Guid runId, string rank) =>
        Expect(Page.GetByTestId($"benchmark-run-row-{runId}").Locator("td").Nth(1)).ToHaveTextAsync(rank, Polled);

    /// <summary>
    ///     A frozen project with an enabled rubric judge and two succeeded, judged runs. Returns the top run's score.
    /// </summary>
    private async Task<int> SeedProjectWithTwoJudgedRunsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBenchmarkStore>();

        _projectId = Guid.NewGuid();
        var project = await store.CreateProjectAsync(new BenchmarkProjectInput(_projectId,
                                     ProjectName,
                                     JsonSerializer.SerializeToUtf8Bytes("Summarise the release notes in three bullet points."),
                                     4096,
                                     Guid.NewGuid()))
                                 .ConfigureAwait(false);

        var policy = BenchmarkE2ETestDoubles.Policy(BenchmarkJudgeRubricDefaults.Default(), ReferenceAnswer);
        var activation = await store.ActivateJudgePolicyAsync(_projectId,
                                        project.Version,
                                        BenchmarkJudgeSerialization.SerializePolicy(policy),
                                        BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy))
                                    .ConfigureAwait(false);

        // One run at a time, primary then its automatic first judging: the work queue is global FIFO, so starting the
        // second run before the first run's judge work is drained would hand the next claim the wrong work item.
        _runA = await SucceededRunAsync(store, activation.Revision.Id, policy, BenchmarkE2ETestDoubles.ModelName, 'a', criterionScore: 9)
            .ConfigureAwait(false);
        var scoreA = (await CompleteQueuedJudgeWorkAsync(store).ConfigureAwait(false))[_runA];
        _runB = await SucceededRunAsync(store, activation.Revision.Id, policy, SecondaryModelName, 'b', criterionScore: 6)
            .ConfigureAwait(false);
        _ = await CompleteQueuedJudgeWorkAsync(store).ConfigureAwait(false);
        return scoreA;
    }

    private async Task<Guid> SucceededRunAsync(IBenchmarkStore store,
        Guid revisionId,
        BenchmarkJudgePolicyV1 policy,
        string modelName,
        char fingerprintFill,
        int criterionScore)
    {
        var project = await store.GetProjectAsync(_projectId).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("The seeded benchmark project disappeared.");
        var run = await store.StartRunAsync(new BenchmarkStartRunCommand(Guid.NewGuid(),
                                 _projectId,
                                 project.Version,
                                 """{"schemaVersion":1}"""u8.ToArray(),
                                 modelName,
                                 LocalModelOrigin.Imported,
                                 "v1:" + new string(fingerprintFill, count: 64),
                                 "E2E Agent",
                                 1,
                                 4096))
                             .ConfigureAwait(false);

        var primary = await store.ClaimNextAsync().ConfigureAwait(false)
                      ?? throw new InvalidOperationException("The seeded run enqueued no primary work.");
        var resolution = await new BenchmarkE2ETestDoubles.JudgeRuntimeResolver().ResolveAsync(policy, CancellationToken.None).ConfigureAwait(false);
        _ = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id,
                           primary.Version,
                           """[{"type":"text","text":"Three bullet points about the release."}]"""u8.ToArray(),
                           LastStreamSequence: 1,
                           EffectiveContextTokens: 4096,
                           DurationMs: 1200,
                           TotalTokens: 128,
                           TokensPerSecond: 32.5,
                           JudgeAttempt: new BenchmarkJudgeAttemptSeed(revisionId,
                               BenchmarkJudgeSerialization.SerializeRuntime(resolution.Runtime),
                               LaunchIntent: resolution.Intent)))
                       .ConfigureAwait(false);
        _criterionScores[run.Id] = criterionScore;
        return run.Id;
    }

    private async Task<int> CompleteQueuedJudgeWorkAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var scores = await CompleteQueuedJudgeWorkAsync(scope.ServiceProvider.GetRequiredService<IBenchmarkStore>()).ConfigureAwait(false);
        return scores[_runA];
    }

    /// <summary>
    ///     Plays the judge queue exactly as <c>BenchmarkJudgeExecutor</c> does — claim, record the launch-ready
    ///     checkpoint with this cohort's execution key, then commit a parsed rubric verdict — with the model call
    ///     replaced by fixed per-criterion scores. The weighted 0..100 roll-up is still the product's own calculator.
    /// </summary>
    private async Task<Dictionary<Guid, int>> CompleteQueuedJudgeWorkAsync(IBenchmarkStore store)
    {
        var scores = new Dictionary<Guid, int>();
        while (await store.ClaimNextAsync().ConfigureAwait(false) is { Kind: BenchmarkWorkKind.Judge } work)
        {
            var attemptId = work.JudgeAttemptId
                            ?? throw new InvalidOperationException("Judge work carried no attempt id.");
            var attempt = await store.GetJudgeAttemptAsync(attemptId).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("The claimed judge attempt disappeared.");
            var revision = await store.GetJudgePolicyRevisionAsync(attempt.PolicyRevisionId).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("The attempt's judge policy revision disappeared.");
            var policy = BenchmarkJudgeSerialization.DeserializePolicy(revision.PolicyJson!.Value.Span);

            // The top run scores 9/10 on every criterion, the other 6/10 — a stable gap so the rank order is a fact
            // about the scores rather than about insertion order.
            var criterionScore = _criterionScores[work.RunId];
            var criteria = policy.Rubric.Criteria
                                 .Select(criterion => new BenchmarkJudgeCriterionScoreV2(criterion.Id,
                                     criterionScore,
                                     $"{criterion.Title}: judged {criterionScore} out of 10."))
                                 .ToArray();
            var score = BenchmarkJudgeScoreCalculator.Compute(policy.Rubric, criteria);

            _ = await store.MarkJudgeLaunchReadyAsync(attemptId,
                               work.QueueSequence,
                               work.Version,
                               new BenchmarkLaunchReceiptCommand("{}",
                                   "{}",
                                   new string('e', count: 64),
                                   new string('r', count: 64),
                                   "e2e-effective-identity",
                                   "cpu",
                                   PlacementOffloaded: null,
                                   PlacementTotal: null,
                                   new string('x', count: 64),
                                   HasAuxAssets: false,
                                   "auto"),
                               ExecutionKey)
                           .ConfigureAwait(false);
            _ = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(work.RunId,
                               work.Version,
                               BenchmarkJudgeSerialization.SerializeResult(new BenchmarkJudgeResultV2(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
                                   criteria,
                                   "The output covers the task at the expected depth.",
                                   score,
                                   BenchmarkE2ETestDoubles.ModelContentFingerprint)),
                               LastStreamSequence: 5,
                               score))
                           .ConfigureAwait(false);
            scores[work.RunId] = score;
        }

        return scores;
    }
}
