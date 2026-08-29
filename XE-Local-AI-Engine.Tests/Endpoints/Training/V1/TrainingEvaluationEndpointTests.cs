namespace XE_Local_AI_Engine.Tests.Endpoints.Training.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the evaluation and comparison routes against the real DI host and an empty
///     database. Every route is Operator-gated, and the lineage refusals (no installed base, nothing promoted, an
///     unknown pairing) surface as 4xx rather than as faults.
/// </summary>
public sealed class TrainingEvaluationEndpointTests
{
    private const string Evaluations = "/api/local/v1/training/evaluations";
    private const string Comparisons = "/api/local/v1/training/comparisons";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task EveryEvaluationAndComparisonRoute_WithoutABearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(Evaluations).ConfigureAwait(false);
        using var byId = await client.GetAsync($"{Evaluations}/{Guid.NewGuid()}").ConfigureAwait(false);
        using var reports = await client.GetAsync(Comparisons).ConfigureAwait(false);
        using var suggest = await client.GetAsync($"{Comparisons}/suggest?trainingRunId={Guid.NewGuid()}").ConfigureAwait(false);
        using var createResponse = await SendAsync(client, HttpMethod.Post, Evaluations).ConfigureAwait(false);
        using var resumeResponse = await SendAsync(client, HttpMethod.Post, $"{Evaluations}/{Guid.NewGuid()}/resume").ConfigureAwait(false);
        using var cancelResponse = await SendAsync(client, HttpMethod.Post, $"{Evaluations}/{Guid.NewGuid()}/cancel").ConfigureAwait(false);
        using var deleteResponse = await SendAsync(client, HttpMethod.Delete, $"{Evaluations}/{Guid.NewGuid()}?expectedVersion=1")
            .ConfigureAwait(false);
        using var reportResponse = await SendAsync(client, HttpMethod.Post, Comparisons).ConfigureAwait(false);

        foreach (var response in new[]
                 {
                     list,
                     byId,
                     reports,
                     suggest,
                     createResponse,
                     resumeResponse,
                     cancelResponse,
                     deleteResponse,
                     reportResponse
                 })
        {
            AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Test]
    public async Task ListEvaluationsAndComparisons_WithOperatorToken_ReturnEmptyListsOnACleanDatabase()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var evaluationsRequest = new HttpRequestMessage(HttpMethod.Get, Evaluations);
        factory.AddNodeBearerToken(evaluationsRequest);
        using var evaluationsResponse = await client.SendAsync(evaluationsRequest).ConfigureAwait(false);

        using var comparisonsRequest = new HttpRequestMessage(HttpMethod.Get, Comparisons);
        factory.AddNodeBearerToken(comparisonsRequest);
        using var comparisonsResponse = await client.SendAsync(comparisonsRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, evaluationsResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, comparisonsResponse.StatusCode);
        await AssertEmptyItemsAsync(evaluationsResponse).ConfigureAwait(false);
        await AssertEmptyItemsAsync(comparisonsResponse).ConfigureAwait(false);
    }

    [Test]
    public async Task GetEvaluationAndComparison_WhenUnknown_ReturnNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var evaluationRequest = new HttpRequestMessage(HttpMethod.Get, $"{Evaluations}/{Guid.NewGuid()}");
        factory.AddNodeBearerToken(evaluationRequest);
        using var evaluationResponse = await client.SendAsync(evaluationRequest).ConfigureAwait(false);

        using var comparisonRequest = new HttpRequestMessage(HttpMethod.Get, $"{Comparisons}/{Guid.NewGuid()}");
        factory.AddNodeBearerToken(comparisonRequest);
        using var comparisonResponse = await client.SendAsync(comparisonRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, evaluationResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.NotFound, comparisonResponse.StatusCode);
    }

    /// <summary>
    ///     The generated client sends the id in the route and ONLY <c>expectedVersion</c> in the body. A DTO that marks
    ///     the route-bound id <c>required</c> turns every such delete into a 400 (the body is deserialized before the
    ///     route value is applied) — found live; an unknown id must reach the service and come back 404.
    /// </summary>
    [Test]
    public async Task DeleteEvaluationAndComparison_WithTheGeneratedClientsBody_ReachTheServiceAndReturnNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var evaluationRequest = new HttpRequestMessage(HttpMethod.Delete, $"{Evaluations}/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new
            {
                expectedVersion = 1
            })
        };
        factory.AddNodeBearerToken(evaluationRequest);
        using var evaluationResponse = await client.SendAsync(evaluationRequest).ConfigureAwait(false);

        using var comparisonRequest = new HttpRequestMessage(HttpMethod.Delete, $"{Comparisons}/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new
            {
                expectedVersion = 1
            })
        };
        factory.AddNodeBearerToken(comparisonRequest);
        using var comparisonResponse = await client.SendAsync(comparisonRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, evaluationResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.NotFound, comparisonResponse.StatusCode);
    }

    [Test]
    public async Task CreateEvaluation_ForAnUnknownRun_IsARejectionNotAFault()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Evaluations)
        {
            Content = JsonContent.Create(new
            {
                trainingRunId = Guid.NewGuid(),
                target = "Base"
            })
        };
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // Operator-facing lineage refusals are 400s, not 500s: "there is no run" is something the operator can act on.
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task SuggestComparison_ForAnUnknownRun_IsARejectionNotAFault()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Comparisons}/suggest?trainingRunId={Guid.NewGuid()}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task CreateComparison_ForUnknownEvaluations_IsARejectionNotAFault()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, Comparisons)
        {
            Content = JsonContent.Create(new
            {
                name = "base vs tuned",
                baseEvaluationRunId = Guid.NewGuid(),
                tunedEvaluationRunId = Guid.NewGuid()
            })
        };
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task ResumeAndCancelEvaluation_WhenUnknown_AreNotAFault()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        // Both are bodyless POSTs whose whole request is the route id — without the declared Accepts they would come
        // back as 415 instead of acting.
        using var resume = new HttpRequestMessage(HttpMethod.Post, $"{Evaluations}/{Guid.NewGuid()}/resume");
        resume.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(resume);
        using var resumeResponse = await client.SendAsync(resume).ConfigureAwait(false);

        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"{Evaluations}/{Guid.NewGuid()}/cancel");
        cancel.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(cancel);
        using var cancelResponse = await client.SendAsync(cancel).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, resumeResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.NotFound, cancelResponse.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route)
    {
        using var request = new HttpRequestMessage(method, route);
        request.Headers.Add("Origin", "http://localhost");
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task AssertEmptyItemsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.True(document.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() == 0,
            "An empty database is an empty list, never a 404.");
    }
}
