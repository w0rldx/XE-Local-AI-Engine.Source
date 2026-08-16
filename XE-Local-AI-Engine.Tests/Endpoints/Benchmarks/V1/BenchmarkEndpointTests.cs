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
        context.Store.ListRunsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([Run()]);
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
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("judgeKvCacheType").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("judgeLaunchReceipt").ValueKind);
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
        new(ProjectId, "Project", Encoding.UTF8.GetBytes("\"Answer exactly.\""), 4096, AgentId, false, null, null, 1, 1,
            isFrozen, 4, 10, 20);

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus primary = BenchmarkPrimaryStatus.Queued,
        BenchmarkJudgeStatus judge = BenchmarkJudgeStatus.Disabled,
        string? output = null,
        BenchmarkRunLaunchIntent? intent = null,
        BenchmarkRunLaunchEvidence? evidence = null) =>
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
            judge,
            null,
            null,
            null,
            3,
            10,
            null,
            null,
            null,
            null,
            20,
            intent,
            null,
            evidence);

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
