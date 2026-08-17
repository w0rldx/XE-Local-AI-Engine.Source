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
    [Arguments("POST", "/projects/00000000-0000-0000-0000-000000000001/runs")]
    [Arguments("GET", "/runs/00000000-0000-0000-0000-000000000002")]
    [Arguments("DELETE", "/runs/00000000-0000-0000-0000-000000000002")]
    [Arguments("POST", "/runs/00000000-0000-0000-0000-000000000002/cancel")]
    [Arguments("PUT", "/runs/00000000-0000-0000-0000-000000000002/score")]
    [Arguments("DELETE", "/runs/00000000-0000-0000-0000-000000000002/score")]
    [Arguments("POST", "/runs/00000000-0000-0000-0000-000000000002/rejudge")]
    [Arguments("PUT", "/projects/00000000-0000-0000-0000-000000000001/judge")]
    [Arguments("POST", "/projects/00000000-0000-0000-0000-000000000001/rejudge")]
    [Arguments("GET", "/rubric-presets")]
    [Arguments("GET", "/eligible-agents?modelName=model")]
    [Arguments("GET", "/eligible-models")]
    public async Task EveryBenchmarkRoute_WithoutOperatorToken_ReturnsUnauthorized(string method, string path)
    {
        await using var context = CreateContext();
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), Api + path);
        if (method is "POST" or "PUT" or "DELETE")
        {
            request.Content = JsonContent.Create(new
            {
            });
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
    public async Task GetRun_ForASucceededJudge_ReturnsTheStoredVerdictOfTheAttempt()
    {
        // Round-tripped through the REAL writer: the stored blob is camelCase, and a reader using default options bound
        // every property to its default — so the API answered a zeroed verdict with a null summary and the frontend
        // rejected the whole run detail as an unexpected shape. Nothing about the run itself had failed.
        await using var context = CreateContext();
        var attemptId = Guid.NewGuid();
        var stored = BenchmarkJudgeSerialization.SerializeResult(new BenchmarkJudgeResultV2(
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
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
        context.RunFreeze.StartAsync(ProjectId, "model", 4, null, Arg.Any<CancellationToken>()).Returns(Run());
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
    public async Task StartRun_CanonicalizesTheRequestedKvCacheTypeBeforeFreezing()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(ProjectId, "model", 4, BenchmarkKvCacheType.Q8_0, Arg.Any<CancellationToken>()).Returns(Run());
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
        _ = context.RunFreeze.Received(1).StartAsync(ProjectId, "model", 4, BenchmarkKvCacheType.Q8_0, Arg.Any<CancellationToken>());
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
        _ = context.RunFreeze.DidNotReceiveWithAnyArgs().StartAsync(Guid.Empty, default!, default, default, default);
    }

    [Test]
    public async Task StartRun_UnsupportedKvCacheType_IsUnprocessable()
    {
        await using var context = CreateContext();
        context.RunFreeze.StartAsync(ProjectId, "model", 4, BenchmarkKvCacheType.Q4_0, Arg.Any<CancellationToken>())
               .Returns<BenchmarkRunRecord>(_ => throw new BenchmarkUnsupportedKvCacheTypeException("A q4_0 KV cache needs a GPU llama.cpp build."));
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
        AssertEx.Equal("v1:aggregate", root.GetProperty("modelGroupKey").GetString());
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

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
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

    private static object ProjectMutation(long? expectedVersion = null) =>
        new
        {
            name = "Project",
            coreTask = "Answer exactly.",
            contextTokens = 4096,
            agentDefinitionId = AgentId,
            judgeEnabled = false,
            judgePromptVersion = 1,
            judgeOutputSchemaVersion = 1,
            expectedVersion
        };

    private static BenchmarkProjectRecord Project(bool isFrozen) =>
        new(ProjectId, "Project", Encoding.UTF8.GetBytes("\"Answer exactly.\""), 4096, AgentId, JudgeEnabled: false,
            CurrentJudgePolicyRevisionId: null, isFrozen, 4, 10, 20);

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus primary = BenchmarkPrimaryStatus.Queued,
        string judgeState = BenchmarkRunJudgeStates.None,
        string? output = null,
        BenchmarkRunLaunchIntent? intent = null,
        BenchmarkRunLaunchEvidence? evidence = null,
        Guid? judgeAttemptId = null) =>
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
            new BenchmarkRunJudgeView(judgeState, judgeAttemptId, null, null, null, null, null, null, null, PolicyCurrent: false, ExecutionCurrent: false, null));

    // Benchmark errors are RFC 7807 problem+json: the operator-safe message is `detail` and the machine-readable
    // BenchmarkErrorCode name rides along as the `code` extension member.
    private static void AssertProblem(HttpResponseMessage response, string body, HttpStatusCode status, BenchmarkErrorCode code, string detail)
    {
        AssertEx.Equal(status, response.StatusCode);
        AssertEx.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
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
        var stored = BenchmarkJudgeSerialization.SerializeResult(new BenchmarkJudgeResultV2(
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
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
        AssertEx.Equal("v1:aggregate", run.GetProperty("modelGroupKey").GetString());
        var cohort = document.RootElement.GetProperty("rankCohort");
        AssertEx.Equal(expected: 1, cohort.GetProperty("rankedCount").GetInt32());
        AssertEx.Equal(expected: 2, cohort.GetProperty("totalScored").GetInt32());
        AssertEx.Equal("cohort-key", cohort.GetProperty("executionKey").GetString());
    }

    private static Context CreateContext() =>
        new();

    private sealed class Context : IAsyncDisposable
    {
        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();
        public IBenchmarkProjectService Projects { get; } = Substitute.For<IBenchmarkProjectService>();
        public IBenchmarkRunFreezeService RunFreeze { get; } = Substitute.For<IBenchmarkRunFreezeService>();
        public IBenchmarkCatalogService Catalog { get; } = Substitute.For<IBenchmarkCatalogService>();
        public IBenchmarkCancellationService Cancellation { get; } = Substitute.For<IBenchmarkCancellationService>();

        public TestServerWebAppFactory Factory { get; }

        public Context()
        {
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
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
