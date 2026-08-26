namespace XE_Local_AI_Engine.Tests.Endpoints.Benchmarks.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkEndpointTests
{
    private const string Api = "/api/local/v1/benchmarks";

    [Test]
    [Arguments("GET", "/projects")]
    [Arguments("POST", "/projects")]
    [Arguments("GET", "/projects/00000000-0000-0000-0000-000000000001")]
    [Arguments("PUT", "/projects/00000000-0000-0000-0000-000000000001")]
    [Arguments("DELETE", "/projects/00000000-0000-0000-0000-000000000001")]
    [Arguments("GET", "/projects/00000000-0000-0000-0000-000000000001/runs")]
    [Arguments("GET", "/projects/00000000-0000-0000-0000-000000000001/cells")]
    [Arguments("POST", "/projects/00000000-0000-0000-0000-000000000001/runs")]
    [Arguments("POST", "/projects/00000000-0000-0000-0000-000000000001/runs/batch")]
    [Arguments("GET", "/runs/00000000-0000-0000-0000-000000000002")]
    [Arguments("DELETE", "/runs/00000000-0000-0000-0000-000000000002")]
    [Arguments("POST", "/runs/00000000-0000-0000-0000-000000000002/cancel")]
    [Arguments("PUT", "/runs/00000000-0000-0000-0000-000000000002/score")]
    [Arguments("DELETE", "/runs/00000000-0000-0000-0000-000000000002/score")]
    [Arguments("POST", "/runs/00000000-0000-0000-0000-000000000002/rejudge")]
    [Arguments("PUT", "/projects/00000000-0000-0000-0000-000000000001/judge")]
    [Arguments("PATCH", "/projects/00000000-0000-0000-0000-000000000001/fidelity")]
    [Arguments("POST", "/projects/00000000-0000-0000-0000-000000000001/rejudge")]
    [Arguments("GET", "/rubric-presets")]
    [Arguments("GET", "/eligible-agents?modelName=model")]
    [Arguments("GET", "/eligible-models")]
    public async Task EveryBenchmarkRoute_WithoutOperatorToken_ReturnsUnauthorized(string method, string path)
    {
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), Api + path);
        if (method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            request.Content = JsonContent.Create(new
            {
            });
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ListCells_ReturnsTheCellTableWithItsPerItemBreakdown()
    {
        await using var context = CreateContext();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project(isFrozen: true));
        var runId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        var itemId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
        context.Store.ListCellsAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkCellPage(
                   [
                       new BenchmarkCellRecord("cell:c:1", "model.gguf", "v1:fp", "q8_0", null, null, 72, 1, null,
                           [new BenchmarkCellItemRecord(runId, itemId, 0, 72, "stop", null)])
                   ],
                   new BenchmarkRankCohort(2, "cohort-key", 3, RankedCount: 1, TotalScored: 1),
                   ScorableItemCount: 3));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}/cells");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"cellKey\":\"cell:c:1\"", StringComparison.Ordinal);
        AssertEx.Contains(body, "\"quality\":72", StringComparison.Ordinal);

        // Not derivable from the cells alone, and it is what makes an item-incomplete badge readable.
        AssertEx.Contains(body, "\"scorableItemCount\":3", StringComparison.Ordinal);
        AssertEx.Contains(body, "\"taskItemId\":\"" + itemId.ToString("D") + "\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task ListCells_ForAnUnknownProject_Is404()
    {
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}/cells");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ListProjects_ReturnsSafeSummariesWithoutCoreTask()
    {
        await using var context = CreateContext();
        context.Store.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns([Project(isFrozen: true)]);
        context.Store.CountRunsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(1);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + "/projects");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"runCount\":1", StringComparison.Ordinal);
        AssertEx.Contains(body, "\"isFrozen\":true", StringComparison.Ordinal);
        AssertEx.False(body.Contains("coreTask", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task GetRun_ReturnsPersistentOutputButNeverRuntimeSnapshot()
    {
        await using var context = CreateContext();
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run(output: "[{\"type\":\"text\",\"text\":\"ok\"}]"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"outputParts\"", StringComparison.Ordinal);
        AssertEx.Contains(body, "\"text\":\"ok\"", StringComparison.Ordinal);
        AssertEx.False(body.Contains("runtimeSnapshot", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(body.Contains("secret-runtime", StringComparison.Ordinal));
    }

    [Test]
    public async Task GetRun_ForAMeasuredRun_ServesTheThroughputSplitWithBothRatesDerived()
    {
        // The rates are NOT persisted — they are derived here from the tokens and milliseconds the runtime reported, so
        // a stored rate can never drift out of step with the counts it was computed from. pp is 123 tokens over
        // 456.5 ms and tg is 89 over 1011.5 ms; a single blended figure could produce neither.
        await using var context = CreateContext();
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>())
               .Returns(Run(BenchmarkPrimaryStatus.Succeeded,
                   throughput: new BenchmarkRunThroughput(TtftMs: 180.25, PromptTokens: 123, PromptMs: 456.5,
                       GenerationTokens: 89, GenerationMs: 1011.5, CachedPromptTokens: 7, SegmentCount: 2)));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var run = document.RootElement;
        AssertEx.Equal(180.25, run.GetProperty("ttftMs").GetDouble());
        AssertEx.Equal(123, run.GetProperty("promptTokens").GetInt32());
        AssertEx.Equal(89, run.GetProperty("generationTokens").GetInt32());
        AssertEx.Equal(7, run.GetProperty("cachedPromptTokens").GetInt32());
        AssertEx.Equal(2, run.GetProperty("segmentCount").GetInt32());
        AssertEx.Equal(123 * 1000d / 456.5, run.GetProperty("promptTokensPerSecond").GetDouble());
        AssertEx.Equal(89 * 1000d / 1011.5, run.GetProperty("generationTokensPerSecond").GetDouble());
    }

    [Test]
    public async Task GetRun_ForAnUnmeasuredRun_LeavesEveryThroughputFieldNull()
    {
        // A runtime that reports no timings must not be given a zero: the API says "not measured", and the UI shows a
        // dash rather than a number nobody produced.
        await using var context = CreateContext();
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run(BenchmarkPrimaryStatus.Succeeded));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        foreach (var field in new[]
                 {
                     "ttftMs",
                     "promptTokens",
                     "promptTokensPerSecond",
                     "generationTokens",
                     "generationTokensPerSecond",
                     "cachedPromptTokens",
                     "segmentCount"
                 })
        {
            AssertEx.Equal(JsonValueKind.Null, document.RootElement.GetProperty(field).ValueKind, $"{field} must be null when nothing measured it.");
        }
    }

    [Test]
    public async Task GetRun_ForASucceededJudge_ReturnsTheStoredVerdictOfTheAttempt()
    {
        // Round-tripped through the REAL writer: the stored blob is camelCase, and a reader using default options bound
        // every property to its default — so the API answered a zeroed verdict with a null summary and the frontend
        // rejected the whole run detail as an unexpected shape. Nothing about the run itself had failed.
        await using var context = CreateContext();
        var attemptId = Guid.NewGuid();
        var stored = BenchmarkJudgeSerialization.SerializeResult(new BenchmarkJudgeResultV2(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            [new BenchmarkJudgeCriterionScoreV2("correctness", 8, "clear and correct")],
            "solid answer",
            80,
            "v1:aggregate"));
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>())
               .Returns(Run(BenchmarkPrimaryStatus.Succeeded, BenchmarkRunJudgeStates.Succeeded, judgeAttemptId: attemptId));
        context.Store.GetJudgeAttemptAsync(attemptId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkJudgeAttemptRecord(attemptId, RunId, 1, Guid.NewGuid(), 1, null, null,
                   BenchmarkJudgeAttemptStatus.Succeeded, stored, 80, null, 0, null, null, 1));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var judge = document.RootElement.GetProperty("judge");
        AssertEx.Equal("solid answer", judge.GetProperty("summary").GetString());
        var criteria = judge.GetProperty("criteria");
        AssertEx.Equal(1, criteria.GetArrayLength());
        AssertEx.Equal("correctness", criteria[0].GetProperty("id").GetString());
        AssertEx.Equal(8, criteria[0].GetProperty("score").GetInt32());
        AssertEx.Equal("clear and correct", criteria[0].GetProperty("rationale").GetString());
    }

    [Test]
    public async Task StartRun_ReturnsAcceptedWithSafeRunDetail()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>()).Returns(Runs());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs",
            new
            {
                modelName = "model",
                expectedProjectVersion = 4
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        AssertEx.Contains(body, "\"modelContentFingerprint\":\"v1:aggregate\"", StringComparison.Ordinal);
        AssertEx.False(body.Contains("runtimeSnapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task StartRun_WithRepeatsAndAWarmup_PassesThemToTheFreezeAndAnswersWithTheFirstRun()
    {
        await using var context = CreateContext();
        var first = Run() with
        {
            RepeatGroupId = Guid.Parse("40000000-0000-0000-0000-000000000004"),
            RepeatIndex = 0,
            IsWarmup = true
        };
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model", 4, null, 3, true),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>()).Returns(Runs(first, Run(), Run()));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs",
            new
            {
                modelName = "model",
                expectedProjectVersion = 4,
                repeatCount = 3,
                warmup = true
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("40000000-0000-0000-0000-000000000004", document.RootElement.GetProperty("repeatGroupId").GetString());
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("repeatIndex").GetInt32());
        AssertEx.True(document.RootElement.GetProperty("isWarmup").GetBoolean(),
            "The answer is the FIRST run of the group — the one that actually starts.");
    }

    [Test]
    public async Task StartRunBatch_EnqueuesEveryCellAndChainsTheProjectVersion()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model-a", 4, null, 2, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>()).Returns(Runs(Run(), Run()));
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model-b", 6, BenchmarkKvCacheType.Q8_0, 2, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>())
               .Returns(Runs(Run(), Run()));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs/batch",
            new
            {
                expectedProjectVersion = 4,
                repeatCount = 2,
                warmup = false,
                items = new[]
                {
                    new
                    {
                        modelName = "model-a",
                        kvCacheType = (string?)null
                    },
                    new
                    {
                        modelName = "model-b",
                        kvCacheType = (string?)"  Q8_0  "
                    }
                }
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var started = document.RootElement.GetProperty("started");
        AssertEx.Equal(expected: 2, started.GetArrayLength());
        AssertEx.Equal(expected: 2, started[0].GetProperty("runIds").GetArrayLength());
        AssertEx.Equal("q8_0", started[1].GetProperty("kvCacheType").GetString(), "The KV type is canonicalized per cell.");
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("rejected").GetArrayLength());
        AssertEx.Equal(expected: 8L, document.RootElement.GetProperty("projectVersion").GetInt64(),
            "Four inserts off version 4 — what the NEXT batch has to present.");

        // Each cell's two inserts bump the project version by two, so the SECOND cell must present version 6. Getting
        // this wrong turns every batch past the first item into a version conflict.
        _ = context.RunFreeze.Received(1).StartAsync(new BenchmarkRunStartRequest(ProjectId, "model-b", 6, BenchmarkKvCacheType.Q8_0, 2, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartRunBatch_SharesOneFreezeScopeAcrossEveryCell()
    {
        // The scope is what makes a matrix affordable: one llama-server capability probe for the request instead of
        // one per cell, and one verification per distinct model instead of one per cell. A per-cell scope would be
        // the old behaviour wearing a new type.
        await using var context = CreateContext();
        var scopes = new List<BenchmarkFreezeScope?>();
        context.RunFreeze.StartAsync(Arg.Any<BenchmarkRunStartRequest>(),
                     Arg.Do<BenchmarkFreezeScope?>(scopes.Add),
                     Arg.Any<CancellationToken>())
               .Returns(Runs());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs/batch", BatchBody("model-a", "model-b"));

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 2, scopes.Count);
        AssertEx.NotNull(scopes[0], "A batch must hand the freeze a scope, never leave each cell to build its own.");
        AssertEx.True(ReferenceEquals(scopes[0], scopes[1]), "Every cell of one request must share the SAME scope.");
    }

    [Test]
    public async Task StartRunBatch_WhenTheTimeBudgetRunsOut_AnswersWithWhatStartedAndNamesTheRest()
    {
        // The freeze is synchronous per cell by design — no background job — so without a budget a large matrix over
        // cold models holds the connection until something times out and the operator cannot tell which cells started.
        // Started at the real clock, not at the provider's own epoch: the host mints this request's token off the
        // SAME TimeProvider, and a token issued in 2026-01-01 is long expired by the time it is validated.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var context = CreateContext(clock);
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model-a", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   clock.Advance(TimeSpan.FromMinutes(2));
                   return Runs();
               });
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs/batch",
            BatchBody("model-a", "model-b", "model-c"));

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 1, document.RootElement.GetProperty("started").GetArrayLength(), "The cell that ran must keep its run ids.");
        var rejected = document.RootElement.GetProperty("rejected");
        AssertEx.Equal(expected: 2, rejected.GetArrayLength());
        AssertEx.Equal(BenchmarkErrorCode.BatchTimeBudget.ToString(), rejected[0].GetProperty("code").GetString());
        AssertEx.Equal("model-b", rejected[0].GetProperty("modelName").GetString(), "A skipped cell is named, so it can be resubmitted.");
        AssertEx.Equal("model-c", rejected[1].GetProperty("modelName").GetString());
        AssertEx.Equal(expected: 5L, document.RootElement.GetProperty("projectVersion").GetInt64(),
            "The version the resubmission has to present is the one the started cell left behind.");

        // Nothing after the budget may be frozen: the point is to stop working, not to report differently.
        _ = context.RunFreeze.DidNotReceive().StartAsync(Arg.Is<BenchmarkRunStartRequest>(value => value.PrimaryModelName == "model-b"),
            Arg.Any<BenchmarkFreezeScope?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartRunBatch_WithOneIneligibleModel_StartsTheRestAndReportsThatCell()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "bad-model", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>())
               .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ => throw new BenchmarkEligibilityException("The selected primary model is not eligible."));
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "good-model", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>()).Returns(Runs());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs/batch",
            new
            {
                expectedProjectVersion = 4,
                items = new[]
                {
                    new
                    {
                        modelName = "bad-model"
                    },
                    new
                    {
                        modelName = "good-model"
                    }
                }
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Per-item, not all-or-nothing: one ineligible model must not cost the operator the rest of the matrix. The
        // refused cell did NOT consume a project version, so the next one still presents 4.
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 1, document.RootElement.GetProperty("started").GetArrayLength());
        AssertEx.Equal("good-model", document.RootElement.GetProperty("started")[0].GetProperty("modelName").GetString());
        var rejected = document.RootElement.GetProperty("rejected");
        AssertEx.Equal(expected: 1, rejected.GetArrayLength());
        AssertEx.Equal("bad-model", rejected[0].GetProperty("modelName").GetString());
        AssertEx.Equal(BenchmarkErrorCode.IneligibleModel.ToString(), rejected[0].GetProperty("code").GetString());
        AssertEx.Equal("The selected primary model is not eligible.", rejected[0].GetProperty("message").GetString());
    }

    [Test]
    public async Task StartRunBatch_WhenTheProjectVersionMoved_FailsTheWholeBatch()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model-a", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>())
               .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ => throw new BenchmarkConflictException("VersionConflict"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs/batch",
            new
            {
                expectedProjectVersion = 4,
                items = new[]
                {
                    new
                    {
                        modelName = "model-a"
                    },
                    new
                    {
                        modelName = "model-b"
                    }
                }
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // A stale version is a fact about the batch: every remaining cell would fail identically, so it is one 409
        // rather than N identical rejections buried in a 200.
        AssertProblem(response, body, HttpStatusCode.Conflict, BenchmarkErrorCode.VersionConflict,
            "The resource version changed. Refresh and retry.");
        _ = context.RunFreeze.DidNotReceive().StartAsync(Arg.Is<BenchmarkRunStartRequest>(request => request.ProjectId == ProjectId
                                                        && request.PrimaryModelName == "model-b"),
            Arg.Any<BenchmarkFreezeScope?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartRunBatch_WhenTheProjectVersionMovedAfterACellStarted_AnswersPartiallyAndKeepsTheStartedRunIds()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model-a", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>()).Returns(Runs());
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model-b", 5, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>())
               .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ => throw new BenchmarkConflictException("VersionConflict"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs/batch",
            new
            {
                expectedProjectVersion = 4,
                items = new[]
                {
                    new
                    {
                        modelName = "model-a"
                    },
                    new
                    {
                        modelName = "model-b"
                    },
                    new
                    {
                        modelName = "model-c"
                    }
                }
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Runs are already queued: a top-level 409 carries no body, so it would discard their ids — the operator could
        // not find them and a retry would enqueue duplicates. The started cell survives, the conflicting cell and every
        // cell after it are reported, and projectVersion is what the resubmission has to present.
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 1, document.RootElement.GetProperty("started").GetArrayLength());
        AssertEx.Equal("model-a", document.RootElement.GetProperty("started")[0].GetProperty("modelName").GetString());
        AssertEx.Equal(expected: 5L, document.RootElement.GetProperty("projectVersion").GetInt64());
        var rejected = document.RootElement.GetProperty("rejected");
        AssertEx.Equal(expected: 2, rejected.GetArrayLength());
        AssertEx.Equal("model-b", rejected[0].GetProperty("modelName").GetString());
        AssertEx.Equal(BenchmarkErrorCode.VersionConflict.ToString(), rejected[0].GetProperty("code").GetString());
        AssertEx.Equal("model-c", rejected[1].GetProperty("modelName").GetString());
        AssertEx.Equal(BenchmarkErrorCode.NotAttempted.ToString(), rejected[1].GetProperty("code").GetString());
        AssertEx.Contains(rejected[1].GetProperty("message").GetString() ?? string.Empty, "resubmit the remaining items",
            StringComparison.Ordinal);

        // The batch stops at the conflict rather than hammering the freeze with cells that would all fail the same way.
        _ = context.RunFreeze.DidNotReceive().StartAsync(Arg.Is<BenchmarkRunStartRequest>(request => request.ProjectId == ProjectId
                                                        && request.PrimaryModelName == "model-c"),
            Arg.Any<BenchmarkFreezeScope?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartRunBatch_WithABlankModelName_RejectsThatCellWithoutTouchingTheFreeze()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model-b", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>()).Returns(Runs());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs/batch",
            new
            {
                expectedProjectVersion = 4,
                items = new[]
                {
                    new
                    {
                        modelName = "   "
                    },
                    new
                    {
                        modelName = "model-b"
                    }
                }
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // A blank name used to reach the freeze and come back as an ArgumentException — a 500 for one operator typo in
        // one cell. It is the same per-cell verdict an ineligible model gets, and the rest of the matrix still starts.
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 1, document.RootElement.GetProperty("started").GetArrayLength());
        AssertEx.Equal("model-b", document.RootElement.GetProperty("started")[0].GetProperty("modelName").GetString());
        var rejected = document.RootElement.GetProperty("rejected");
        AssertEx.Equal(expected: 1, rejected.GetArrayLength());
        AssertEx.Equal(BenchmarkErrorCode.InvalidRequest.ToString(), rejected[0].GetProperty("code").GetString());
        _ = context.RunFreeze.DidNotReceive().StartAsync(Arg.Is<BenchmarkRunStartRequest>(request => request.ProjectId == ProjectId
                                                        && request.PrimaryModelName == "   "),
            Arg.Any<BenchmarkFreezeScope?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartRunBatch_WithNoItems_IsABadRequest()
    {
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs/batch",
            new
            {
                expectedProjectVersion = 4,
                items = Array.Empty<object>()
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _ = context.RunFreeze.DidNotReceiveWithAnyArgs().StartAsync(default!, default, default);
    }

    [Test]
    public async Task StartRun_CanonicalizesTheRequestedKvCacheTypeBeforeFreezing()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model", 4, BenchmarkKvCacheType.Q8_0, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>()).Returns(Runs());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs",
            new
            {
                modelName = "model",
                expectedProjectVersion = 4,
                kvCacheType = "  Q8_0  "
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _ = context.RunFreeze.Received(1).StartAsync(new BenchmarkRunStartRequest(ProjectId, "model", 4, BenchmarkKvCacheType.Q8_0, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartRun_UnknownKvCacheType_IsABadRequest()
    {
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs",
            new
            {
                modelName = "model",
                expectedProjectVersion = 4,
                kvCacheType = "q3_k"
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.BadRequest, BenchmarkErrorCode.InvalidRequest, "The requested KV-cache type is not supported.");
        _ = context.RunFreeze.DidNotReceiveWithAnyArgs().StartAsync(default!, default, default);
    }

    [Test]
    public async Task StartRun_UnsupportedKvCacheType_IsUnprocessable()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model", 4, BenchmarkKvCacheType.Q4_0, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>())
               .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ => throw new BenchmarkUnsupportedKvCacheTypeException("A q4_0 KV cache needs a GPU llama.cpp build."));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs",
            new
            {
                modelName = "model",
                expectedProjectVersion = 4,
                kvCacheType = "q4_0"
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.UnprocessableEntity, BenchmarkErrorCode.UnsupportedKvCacheType,
            "A q4_0 KV cache needs a GPU llama.cpp build.");
    }

    [Test]
    public async Task StartRun_WhenTheModelCannotBeVerifiedAtFreeze_IsUnprocessableRatherThanAFailure()
    {
        // Verification moved OFF the catalog listing onto freeze, so a model whose files no longer match its registry
        // entry now lists happily and fails only here. The freeze service maps that store failure to an eligibility
        // refusal, which is the 422 this route declares; unmapped it escaped as a 500, and inside a batch it killed
        // every cell after it instead of rejecting one.
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "model", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>())
               .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ =>
                   throw new BenchmarkEligibilityException("The selected model could not be verified against its installed registry entry."));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs",
            new
            {
                modelName = "model",
                expectedProjectVersion = 4
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.UnprocessableEntity, BenchmarkErrorCode.IneligibleModel,
            "The selected model could not be verified against its installed registry entry.");
    }

    [Test]
    public async Task StartRun_WhenTheRequestedModelDisappeared_MapsContextualKeyNotFoundWithoutLeakingItsMessage()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(new BenchmarkRunStartRequest(ProjectId, "missing-model", 4, null, 1, false),
                     Arg.Any<BenchmarkFreezeScope?>(),
                     Arg.Any<CancellationToken>())
               .Returns<IReadOnlyList<BenchmarkRunRecord>>(_ => throw new KeyNotFoundException("secret registry path: /node/models/private"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/runs", new
        {
            modelName = "missing-model",
            expectedProjectVersion = 4
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.NotFound, BenchmarkErrorCode.NotFound,
            "The requested benchmark resource was not found.");
        AssertEx.False(body.Contains("secret registry path", StringComparison.Ordinal),
            "The route-specific KeyNotFound mapping must keep provider context out of the response.");
    }

    [Test]
    public async Task GetRun_ExposesTheIntendedAndEffectiveLaunchFieldsAndTheDecodedReceipt()
    {
        await using var context = CreateContext();
        var receipt = "{\"executableSha256\":\"exe-sha\",\"auxAssets\":{\"hasLora\":true,\"hasMmproj\":false,\"hasDraft\":false}}";
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>())
               .Returns(Run(intent: new BenchmarkRunLaunchIntent("cuda", BenchmarkKvCacheType.Q8_0, BenchmarkKvCacheType.SourceAuto,
                       null, "on", "intended-identity", "manifest-sha"),
                   evidence: new BenchmarkRunLaunchEvidence(Encoding.UTF8.GetBytes(receipt),
                       Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"),
                       "receipt-hash",
                       "environment-hash",
                       "effective-identity",
                       "cuda",
                       33,
                       33,
                       "exe-sha",
                       true,
                       BenchmarkKvCacheType.SourceAuto)));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        AssertEx.Equal("q8_0", root.GetProperty("primaryKvCacheType").GetString());
        AssertEx.Equal("auto", root.GetProperty("primaryKvCacheTypeSource").GetString());
        AssertEx.Equal("intended-identity", root.GetProperty("primaryIntendedLaunchIdentity").GetString());
        AssertEx.Equal("effective-identity", root.GetProperty("primaryEffectiveLaunchIdentity").GetString());
        AssertEx.Equal("cuda", root.GetProperty("primaryEffectiveBackend").GetString());
        AssertEx.Equal(expected: 33, root.GetProperty("primaryPlacementOffloaded").GetInt32());
        AssertEx.Equal("exe-sha", root.GetProperty("primaryExecutableSha256").GetString());
        AssertEx.True(root.GetProperty("primaryHasAuxAssets").GetBoolean(), "An adapter recorded in the receipt must surface as an aux-asset flag.");
        AssertEx.Equal("receipt-hash", root.GetProperty("primaryReceiptHash").GetString());
        AssertEx.Equal("environment-hash", root.GetProperty("primaryEnvironmentFactsHash").GetString());
        AssertEx.Equal("exe-sha", root.GetProperty("primaryLaunchReceipt").GetProperty("executableSha256").GetString());
        AssertEx.Equal(expected: 1, root.GetProperty("primaryEnvironmentFacts").GetProperty("schemaVersion").GetInt32());

        // Primary-only: the judge's own launch evidence belongs to its attempt, and the run projects a derived object.
        AssertEx.Equal("none", root.GetProperty("judge").GetProperty("state").GetString());
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("judge").GetProperty("score").ValueKind);
        AssertEx.Equal("none", root.GetProperty("qualityScoreSource").GetString());
        AssertEx.Equal("model", root.GetProperty("modelGroupKey").GetString(),
            "The group key is the base model, not the content fingerprint — quants of one model must land in one group.");
    }

    [Test]
    public async Task StartRun_OpenApiDocumentsAcceptedResponse()
    {
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var response = await client.GetAsync("/openapi/local/v1/v1.json").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);
        var responses = document.RootElement
                                .GetProperty("paths")
                                .GetProperty($"{Api}/projects/{{projectId}}/runs")
                                .GetProperty("post")
                                .GetProperty("responses");

        AssertEx.True(responses.TryGetProperty("202", out _), "The start-run operation must document its accepted response.");
    }

    [Test]
    public async Task GetRun_ExposesTheStopReasonAndTheTruncatedRankExclusion()
    {
        await using var context = CreateContext();
        context.Store.GetRunAsync(RunId, Arg.Any<CancellationToken>())
               .Returns(Run(BenchmarkPrimaryStatus.Succeeded,
                   output: "[{\"kind\":\"output\",\"content\":\"cut\"}]",
                   primaryStopReason: "length",
                   rankExclusionReason: BenchmarkRunJudgeStates.ReasonTruncated));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/runs/{RunId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("length", document.RootElement.GetProperty("primaryStopReason").GetString());
        AssertEx.Equal("truncated", document.RootElement.GetProperty("rankExclusionReason").GetString());
        AssertEx.Equal("Succeeded", document.RootElement.GetProperty("primaryStatus").GetString(),
            "A truncated run is flagged, never failed.");
    }

    [Test]
    public async Task GetProject_ExposesTheRunBudgetsAndTheMutationCarriesThemToTheDraft()
    {
        await using var context = CreateContext();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(Project(isFrozen: false, maxOutputTokens: 2048, invocationTimeoutSeconds: 1800));
        BenchmarkProjectDraft? draft = null;
        context.Projects.CreateAsync(Arg.Do<BenchmarkProjectDraft>(value => draft = value), Arg.Any<CancellationToken>())
               .Returns(Project(isFrozen: false, maxOutputTokens: 1024));
        using var client = context.Factory.CreateClient();

        using var getRequest = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}");
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);
        var getBody = await getResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var createRequest = Authorized(context.Factory, HttpMethod.Post, Api + "/projects",
            ProjectMutation(maxOutputTokens: 1024, invocationTimeoutSeconds: 1200));
        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        using var document = JsonDocument.Parse(getBody);
        AssertEx.Equal<int?>(2048, document.RootElement.GetProperty("maxOutputTokens").GetInt32());
        AssertEx.Equal<int?>(1800, document.RootElement.GetProperty("invocationTimeoutSeconds").GetInt32());
        AssertEx.Equal<int?>(1024, AssertEx.NotNull(draft).MaxOutputTokens, "The request's budget must reach the service draft.");
        AssertEx.Equal<int?>(1200, draft!.InvocationTimeoutSeconds, "The request's generation timeout must reach the service draft.");
    }

    [Test]
    public async Task CreateProject_WhenAgentIsIneligible_ReturnsUnprocessableEntity()
    {
        await using var context = CreateContext();
        context.Projects.CreateAsync(Arg.Any<BenchmarkProjectDraft>(), Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkProjectRecord>>(_ => throw new BenchmarkEligibilityException("The selected agent is not eligible."));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + "/projects", ProjectMutation());
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.UnprocessableEntity, BenchmarkErrorCode.IneligibleAgent, "The selected agent is not eligible.");
    }

    [Test]
    public async Task UpdateProject_WhenFrozen_ReturnsConflictWithSafeCode()
    {
        await using var context = CreateContext();
        context.Projects.UpdateAsync(ProjectId, 4, Arg.Any<BenchmarkProjectDraft>(), Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkProjectRecord>>(_ => throw new BenchmarkConflictException("ProjectFrozen"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/projects/{ProjectId}", ProjectMutation(expectedVersion: 4));
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.Conflict, BenchmarkErrorCode.ProjectFrozen, "The benchmark project has runs and is frozen.");
    }

    [Test]
    public async Task ScoreRun_WhenScoreIsInvalid_ReturnsBadRequest()
    {
        await using var context = CreateContext();
        context.Store.SetUserScoreAsync(RunId, 6, 3, Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkRunRecord>>(_ => throw new BenchmarkValidationException("Score must be between 1 and 5."));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/score", new
        {
            score = 6,
            expectedVersion = 3
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.BadRequest, BenchmarkErrorCode.InvalidRequest, "Score must be between 1 and 5.");
    }

    [Test]
    public async Task GetProject_WithAJudgePolicyStoredUnderAnOlderPromptVersion_StillReadsAndFlagsIt()
    {
        // The read path must tolerate a stored version this build no longer judges under. When it did not, the project
        // detail 500'd, the whole header disappeared from the UI ("Select a benchmark project"), and the re-save that
        // heals the revision became unreachable.
        await using var context = CreateContext();
        ArrangeOutdatedJudgePolicy(context);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var judge = document.RootElement.GetProperty("judge");
        AssertEx.True(judge.GetProperty("enabled").GetBoolean(), "An outdated prompt version does not disable the judge.");
        AssertEx.Equal(BenchmarkJudgePolicyVersions.PromptVersion - 1, judge.GetProperty("promptVersion").GetInt32());
        AssertEx.True(judge.GetProperty("promptVersionOutdated").GetBoolean(), "The client needs the flag to offer the re-save.");
    }

    [Test]
    public async Task GetProject_WithACurrentJudgePolicy_DoesNotFlagThePromptVersion()
    {
        await using var context = CreateContext();
        ArrangeOutdatedJudgePolicy(context, promptVersion: BenchmarkJudgePolicyVersions.PromptVersion);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.False(document.RootElement.GetProperty("judge").GetProperty("promptVersionOutdated").GetBoolean());
    }

    /// <summary>A project whose CURRENT judge revision was stored under <paramref name="promptVersion" />.</summary>
    private static void ArrangeOutdatedJudgePolicy(Context context, int? promptVersion = null)
    {
        var policy = new BenchmarkJudgePolicyV1(new BenchmarkJudgePolicyModelV1("judge.gguf", "v1:" + new string('a', 64), ["aaa"]),
            4096,
            promptVersion ?? BenchmarkJudgePolicyVersions.PromptVersion - 1,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            BenchmarkJudgeRubricDefaults.Default(),
            ReferenceAnswer: null);
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project(isFrozen: false));
        context.Store.GetCurrentJudgePolicyRevisionAsync(ProjectId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkJudgePolicyRevisionRecord(Guid.NewGuid(), ProjectId, 2,
                   BenchmarkJudgeSerialization.SerializePolicy(policy), new string('h', 64), "cohort-key", 3, 10));
    }

    [Test]
    public async Task GetProject_ServesTheFidelitySettingsAndTheDigestTheyRecompute()
    {
        // The five persisted columns plus two derived reads: the chunk count that actually runs when the operator
        // left it at the default, and the comparability digest a stored KLD figure is gated against. The digest is
        // taken from the ONE builder every other display gate reads; a second expression here is the bug that lets
        // numbers measured against different corpora compare as equal.
        await using var context = CreateContext();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project(isFrozen: false, fidelity: true));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var project = document.RootElement;
        AssertEx.True(project.GetProperty("fidelityEnabled").GetBoolean());
        AssertEx.True(project.GetProperty("fidelityKldEnabled").GetBoolean());
        AssertEx.Equal(JsonValueKind.Null, project.GetProperty("fidelityChunks").ValueKind);
        AssertEx.Equal(BenchmarkFidelityPolicy.DefaultChunks, project.GetProperty("fidelityChunksEffective").GetInt32());
        AssertEx.Equal(BaseModelName, project.GetProperty("fidelityKldBaseModelName").GetString());
        AssertEx.Equal(BaseFingerprint, project.GetProperty("fidelityKldBaseFingerprint").GetString());
        AssertEx.Equal(BenchmarkKldCacheKey.Create(BaseFingerprint, BenchmarkFidelityCorpus.Require().Sha256, BenchmarkFidelityPolicy.DefaultChunks).Digest,
            project.GetProperty("fidelityKldExpectedDigest").GetString());
    }

    [Test]
    public async Task GetProject_WithoutKldEnabled_ServesNoExpectedDigest()
    {
        // Null is the honest answer: a project that measures no KL divergence has nothing for a stored figure to be
        // compared against, and an emitted digest would invite a comparison that means nothing.
        await using var context = CreateContext();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project(isFrozen: false));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        AssertEx.False(document.RootElement.GetProperty("fidelityEnabled").GetBoolean());
        AssertEx.Equal(JsonValueKind.Null, document.RootElement.GetProperty("fidelityKldExpectedDigest").ValueKind);
    }

    [Test]
    public async Task ListProjects_DoesNotCarryTheFidelitySettings()
    {
        // The listing stays a flat scan. The fidelity block is detail-only, exactly as coreTask and the judge are.
        await using var context = CreateContext();
        context.Store.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns([Project(isFrozen: false, fidelity: true)]);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + "/projects");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(body.Contains("fidelity", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task PatchProjectFidelity_OnAFrozenProject_SucceedsAndReportsWhatItQueued()
    {
        // The one project write the freeze does not refuse. If this ever starts 409-ing, an operator whose project
        // has runs can never turn fidelity on at all, which is the hole this route exists to close.
        await using var context = CreateContext();
        var queued = Guid.NewGuid();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project(isFrozen: true, fidelity: true));
        context.Store.CountRunsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(3);
        BenchmarkProjectFidelitySettings? settings = null;
        var measureExisting = false;
        context.Projects.UpdateFidelityAsync(ProjectId, 4, Arg.Do<BenchmarkProjectFidelitySettings>(value => settings = value),
                   Arg.Do<bool>(value => measureExisting = value), Arg.Any<CancellationToken>())
               .Returns(new BenchmarkProjectFidelityChange(Project(isFrozen: true, fidelity: true), [queued]));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Patch, Api + $"/projects/{ProjectId}/fidelity",
            new
            {
                projectId = ProjectId,
                expectedVersion = 4,
                fidelityEnabled = true,
                fidelityKldEnabled = false,
                measureExisting = true
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(1, document.RootElement.GetProperty("enqueuedCount").GetInt32());
        AssertEx.Equal(queued, document.RootElement.GetProperty("enqueuedRunIds")[0].GetGuid());
        AssertEx.True(document.RootElement.GetProperty("project").GetProperty("isFrozen").GetBoolean());
        AssertEx.True(document.RootElement.GetProperty("project").GetProperty("fidelityEnabled").GetBoolean());
        AssertEx.True(AssertEx.NotNull(settings).Enabled);
        AssertEx.False(settings!.KldEnabled);
        AssertEx.True(measureExisting, "The flag must reach the service, or the operator's opt-in silently does nothing.");
    }

    [Test]
    public async Task GetProject_WhenMissing_ReturnsProblemDetailsNotFound()
    {
        await using var context = CreateContext();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns((BenchmarkProjectRecord?)null);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.NotFound, BenchmarkErrorCode.NotFound, "The requested benchmark resource was not found.");
    }

    [Test]
    public async Task CancelPrimary_AfterPrimarySucceeded_MapsCancellationServiceConflict()
    {
        await using var context = CreateContext();
        context.Cancellation.CancelAsync(RunId,
                   3,
                   BenchmarkCancellationTarget.Primary,
                   Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkRunRecord>>(_ => throw new BenchmarkConflictException("PrimaryAlreadySucceeded"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/runs/{RunId}/cancel",
            new
            {
                target = "Primary",
                expectedVersion = 3
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.Conflict, BenchmarkErrorCode.InvalidLifecycleTransition,
            "The benchmark lifecycle transition is not allowed.");
        await context.Cancellation.Received(1).CancelAsync(RunId,
            3,
            BenchmarkCancellationTarget.Primary,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EligibleModels_SerializesSharedOriginExactlyLowercase()
    {
        await using var context = CreateContext();
        context.Catalog.ListEligibleModelsAsync(null, Arg.Any<CancellationToken>())
               .Returns([new BenchmarkEligibleModel("model", 8192, null, LocalModelOrigin.Imported, "v1:aggregate", true)]);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + "/eligible-models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"origin\":\"imported\"", StringComparison.Ordinal);
        AssertEx.Contains(body, "\"modelContentFingerprint\":\"v1:aggregate\"", StringComparison.Ordinal);
    }

    private static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid RunId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid AgentId = Guid.Parse("30000000-0000-0000-0000-000000000003");

    private static object ProjectMutation(long? expectedVersion = null, int? maxOutputTokens = null, int? invocationTimeoutSeconds = null) =>
        new
        {
            name = "Project",
            coreTask = "Answer exactly.",
            contextTokens = 4096,
            maxOutputTokens,
            invocationTimeoutSeconds,
            agentDefinitionId = AgentId,
            judgeEnabled = false,
            judgePromptVersion = 1,
            judgeOutputSchemaVersion = 1,
            expectedVersion
        };

    private const string BaseModelName = "base.gguf";
    private const string BaseFingerprint = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static BenchmarkProjectRecord Project(bool isFrozen,
        int? maxOutputTokens = null,
        int? invocationTimeoutSeconds = null,
        bool fidelity = false) =>
        new(ProjectId, "Project", Encoding.UTF8.GetBytes("\"Answer exactly.\""), 4096, AgentId, JudgeEnabled: false,
            CurrentJudgePolicyRevisionId: null, isFrozen, 4, 10, 20, maxOutputTokens, invocationTimeoutSeconds,
            ReasoningBudgetTokens: null, fidelity, fidelity, FidelityChunks: null,
            fidelity ? BaseModelName : null, fidelity ? BaseFingerprint : null);

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus primary = BenchmarkPrimaryStatus.Queued,
        string judgeState = BenchmarkRunJudgeStates.None,
        string? output = null,
        BenchmarkRunLaunchIntent? intent = null,
        BenchmarkRunLaunchEvidence? evidence = null,
        Guid? judgeAttemptId = null,
        string? primaryStopReason = null,
        string? rankExclusionReason = null,
        BenchmarkRunThroughput? throughput = null) =>
        new(RunId,
            ProjectId,
            Encoding.UTF8.GetBytes("secret-runtime"),
            "model",
            LocalModelOrigin.Imported,
            "v1:aggregate",
            "Agent",
            2,
            4096,
            primary,
            null,
            null,
            null,
            null,
            output is null ? null : Encoding.UTF8.GetBytes(output),
            0,
            null,
            null,
            3,
            10,
            null,
            null,
            20,
            intent,
            evidence,
            PrimaryStopReason: primaryStopReason,
            Judge: new BenchmarkRunJudgeView(judgeState, judgeAttemptId, null, null, null, null, null, null, null, PolicyCurrent: false,
                ExecutionCurrent: false, rankExclusionReason),
            Throughput: throughput);

    /// <summary>What the freeze service returns now: a group of runs, one for a plain single start.</summary>
    private static IReadOnlyList<BenchmarkRunRecord> Runs(params BenchmarkRunRecord[] runs) =>
        runs.Length == 0 ? [Run()] : runs;

    // Benchmark errors are RFC 7807 problem+json: the operator-safe message is `detail` and the machine-readable
    // BenchmarkErrorCode name rides along as the `code` extension member.
    private static void AssertProblem(HttpResponseMessage response, string body, HttpStatusCode status, BenchmarkErrorCode code, string detail)
    {
        AssertEx.Equal(status, response.StatusCode);
        AssertEx.Equal("application/problem+json", response.Content.Headers.ContentType?.ToString());
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        AssertEx.Equal(code.ToString(), document.RootElement.GetProperty("code").GetString());
        AssertEx.Equal(detail, document.RootElement.GetProperty("detail").GetString());
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string path, object? content = null)
    {
        var request = new HttpRequestMessage(method, path);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        if (content is not null)
        {
            request.Content = JsonContent.Create(content);
        }

        return request;
    }

    [Test]
    public async Task ScoreRun_WithAnOmittedScore_IsRejectedRatherThanScoredZero()
    {
        // Zero is a valid operator verdict now, so an absent field must not arrive as one.
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/score", new
        {
            expectedVersion = 3
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _ = context.Store.DidNotReceive().SetUserScoreAsync(Arg.Any<Guid>(), Arg.Any<int?>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ScoreRun_AcceptsZeroAndClearsWithDelete()
    {
        await using var context = CreateContext();
        context.Store.SetUserScoreAsync(RunId, Arg.Any<int?>(), 3, Arg.Any<CancellationToken>()).Returns(Run());
        using var client = context.Factory.CreateClient();
        using var scoreRequest = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/score", new
        {
            score = 0,
            expectedVersion = 3
        });
        using var scoreResponse = await client.SendAsync(scoreRequest).ConfigureAwait(false);
        using var clearRequest = Authorized(context.Factory, HttpMethod.Delete, Api + $"/runs/{RunId}/score", new
        {
            expectedVersion = 3
        });
        using var clearResponse = await client.SendAsync(clearRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, scoreResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
        _ = context.Store.Received(1).SetUserScoreAsync(RunId, 0, 3, Arg.Any<CancellationToken>());
        _ = context.Store.Received(1).SetUserScoreAsync(RunId, null, 3, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ScoreRun_ReturnsTheJudgeVerdictTheGetWouldShow()
    {
        // A score is an operator override beside the judging, not a replacement for it: dropping the verdict from the
        // mutation response made the just-scored run render as "not judged" until the next refresh.
        await using var context = CreateContext();
        var attemptId = Guid.NewGuid();
        var stored = BenchmarkJudgeSerialization.SerializeResult(new BenchmarkJudgeResultV2(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            [new BenchmarkJudgeCriterionScoreV2("correctness", 8, "clear and correct")],
            "solid answer",
            80,
            "v1:aggregate"));
        context.Store.SetUserScoreAsync(RunId, Arg.Any<int?>(), 3, Arg.Any<CancellationToken>())
               .Returns(Run(BenchmarkPrimaryStatus.Succeeded, BenchmarkRunJudgeStates.Succeeded, judgeAttemptId: attemptId));
        context.Store.GetJudgeAttemptAsync(attemptId, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkJudgeAttemptRecord(attemptId, RunId, 1, Guid.NewGuid(), 1, null, null,
                   BenchmarkJudgeAttemptStatus.Succeeded, stored, 80, null, 0, null, null, 1));
        using var client = context.Factory.CreateClient();
        using var scoreRequest = Authorized(context.Factory, HttpMethod.Put, Api + $"/runs/{RunId}/score", new
        {
            score = 42,
            expectedVersion = 3
        });
        using var scoreResponse = await client.SendAsync(scoreRequest).ConfigureAwait(false);
        var scored = await scoreResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var clearRequest = Authorized(context.Factory, HttpMethod.Delete, Api + $"/runs/{RunId}/score", new
        {
            expectedVersion = 3
        });
        using var clearResponse = await client.SendAsync(clearRequest).ConfigureAwait(false);
        var cleared = await clearResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, scoreResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
        foreach (var body in new[]
                 {
                     scored,
                     cleared
                 })
        {
            using var document = JsonDocument.Parse(body);
            var judge = document.RootElement.GetProperty("judge");
            AssertEx.Equal("solid answer", judge.GetProperty("summary").GetString());
            AssertEx.Equal(1, judge.GetProperty("criteria").GetArrayLength());
        }
    }

    [Test]
    public async Task UpdateJudgePolicy_WithoutConfirmation_ReturnsRejudgeRequired()
    {
        await using var context = CreateContext();
        context.Projects.UpdateJudgePolicyAsync(ProjectId, 4, Arg.Any<BenchmarkJudgePolicyDraft?>(), false, Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkJudgePolicyChange>>(_ => throw new BenchmarkConflictException("RejudgeRequired"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/projects/{ProjectId}/judge", new
        {
            policy = new
            {
                modelName = "judge.gguf",
                contextTokens = 4096
            },
            expectedVersion = 4,
            confirmRejudge = false
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.Conflict, BenchmarkErrorCode.RejudgeRequired,
            "Changing the judge re-scores every run of this project. Confirm the re-judge to continue.");
    }

    [Test]
    public async Task UpdateJudgePolicy_WhenConfirmed_ReturnsTheProjectAndTheRunsItQueued()
    {
        var enqueued = Guid.NewGuid();
        await using var context = CreateContext();
        context.Store.CountRunsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(2);
        context.Projects.UpdateJudgePolicyAsync(ProjectId, 4, Arg.Any<BenchmarkJudgePolicyDraft?>(), true, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkJudgePolicyChange(Project(isFrozen: true), [enqueued], 3));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, Api + $"/projects/{ProjectId}/judge", new
        {
            policy = new
            {
                modelName = "judge.gguf",
                contextTokens = 4096,
                rubric = new
                {
                    version = 1,
                    criteria = new[]
                    {
                        new
                        {
                            id = "correctness",
                            title = "Correctness",
                            description = "Is it right?",
                            weight = 40
                        }
                    }
                }
            },
            expectedVersion = 4,
            confirmRejudge = true
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(enqueued.ToString(), document.RootElement.GetProperty("enqueuedRunIds")[0].GetString());
        AssertEx.Equal(expected: 3, document.RootElement.GetProperty("cohortGeneration").GetInt32());
        AssertEx.Equal(ProjectId.ToString(), document.RootElement.GetProperty("project").GetProperty("id").GetString());
    }

    [Test]
    public async Task RejudgeProject_WhileAJudgingIsActive_ReturnsConflict()
    {
        await using var context = CreateContext();
        context.Projects.RejudgeProjectAsync(ProjectId, 4, Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkJudgePolicyChange>>(_ => throw new BenchmarkConflictException("JudgeAttemptsActive"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/projects/{ProjectId}/rejudge", new
        {
            expectedVersion = 4
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.Conflict, BenchmarkErrorCode.JudgeAttemptsActive,
            "A judging of this project is still running. Wait for it or cancel it first.");
    }

    [Test]
    public async Task RejudgeRun_WhenTheProjectHasNoJudge_ReturnsConflict()
    {
        await using var context = CreateContext();
        context.Projects.RejudgeRunAsync(RunId, 3, false, Arg.Any<CancellationToken>())
               .Returns<Task<BenchmarkJudgeAttemptRecord>>(_ => throw new BenchmarkConflictException("JudgeDisabled"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, Api + $"/runs/{RunId}/rejudge", new
        {
            expectedVersion = 3,
            force = false
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertProblem(response, body, HttpStatusCode.Conflict, BenchmarkErrorCode.JudgeDisabled,
            "This project has no judge policy to judge under.");
    }

    [Test]
    public async Task RubricPresets_ReturnsTheThreeRubricsTheFormOffers()
    {
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + "/rubric-presets");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        foreach (var preset in new[]
                 {
                     "default",
                     "programming",
                     "reasoning"
                 })
        {
            var criteria = document.RootElement.GetProperty(preset).GetProperty("criteria");
            AssertEx.Equal(expected: 5, criteria.GetArrayLength(), $"The {preset} preset must offer the full rubric.");
            AssertEx.Equal("correctness", criteria[0].GetProperty("id").GetString());
        }

        // The two server-side presets do NOT share those ids and weights, because a verifiable criterion is a
        // different question. codeExecution is a single criterion on purpose: the hidden tests pass or they do not.
        var verifiable = document.RootElement.GetProperty("verifiable").GetProperty("criteria");
        AssertEx.True(verifiable.GetArrayLength() > 0);

        var execution = document.RootElement.GetProperty("codeExecution").GetProperty("criteria");
        AssertEx.Equal(expected: 1, execution.GetArrayLength());
        AssertEx.Equal("pythonTests", execution[0].GetProperty("kind").GetString());
        AssertEx.Equal(expected: 100, execution[0].GetProperty("weight").GetInt32());
    }

    [Test]
    public async Task ListRuns_ProjectsTheRankCohortAndTheRunsQualityScore()
    {
        await using var context = CreateContext();
        context.Store.GetProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Project(isFrozen: true));
        context.Store.ListRunsAsync(ProjectId, 0, 50, null, true, Arg.Any<CancellationToken>())
               .Returns(new BenchmarkRunPage([
                       Run() with
                       {
                           QualityScore = 73,
                           QualityScoreSource = "judge",
                           Rank = 1
                       }
                   ],
                   TotalCount: 1,
                   new BenchmarkRankCohort(2, "cohort-key", 3, RankedCount: 1, TotalScored: 2)));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, Api + $"/projects/{ProjectId}/runs?page=1&pageSize=50");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var run = document.RootElement.GetProperty("items")[0];
        AssertEx.Equal(expected: 73, run.GetProperty("qualityScore").GetInt32());
        AssertEx.Equal("judge", run.GetProperty("qualityScoreSource").GetString());
        AssertEx.Equal(expected: 1, run.GetProperty("rank").GetInt32());
        AssertEx.Equal("model", run.GetProperty("modelGroupKey").GetString());
        var cohort = document.RootElement.GetProperty("rankCohort");
        AssertEx.Equal(expected: 1, cohort.GetProperty("rankedCount").GetInt32());
        AssertEx.Equal(expected: 2, cohort.GetProperty("totalScored").GetInt32());
        AssertEx.Equal("cohort-key", cohort.GetProperty("executionKey").GetString());
    }

    private static object BatchBody(params string[] modelNames) =>
        new
        {
            expectedProjectVersion = 4,
            repeatCount = 1,
            warmup = false,
            items = modelNames.Select(static modelName => new
            {
                modelName,
                kvCacheType = (string?)null
            }).ToArray()
        };

    private static Context CreateContext(ManualTimeProvider? clock = null) =>
        new(clock);

    private sealed class Context : IAsyncDisposable
    {
        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();
        public IBenchmarkProjectService Projects { get; } = Substitute.For<IBenchmarkProjectService>();
        public IBenchmarkRunFreezeService RunFreeze { get; } = Substitute.For<IBenchmarkRunFreezeService>();
        public IBenchmarkCatalogService Catalog { get; } = Substitute.For<IBenchmarkCatalogService>();
        public IBenchmarkCancellationService Cancellation { get; } = Substitute.For<IBenchmarkCancellationService>();

        public TestServerWebAppFactory Factory { get; }

        public Context(ManualTimeProvider? clock = null)
        {
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    if (clock is not null)
                    {
                        services.RemoveAll<TimeProvider>();
                        services.AddSingleton<TimeProvider>(clock);
                    }

                    services.RemoveAll<IBenchmarkStore>();
                    services.RemoveAll<IBenchmarkProjectService>();
                    services.RemoveAll<IBenchmarkRunFreezeService>();
                    services.RemoveAll<IBenchmarkCatalogService>();
                    services.RemoveAll<IBenchmarkCancellationService>();
                    services.AddSingleton(Store);
                    services.AddSingleton(Projects);
                    services.AddSingleton(RunFreeze);
                    services.AddSingleton(Catalog);
                    services.AddSingleton(Cancellation);
                }
            };
        }

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }
}
