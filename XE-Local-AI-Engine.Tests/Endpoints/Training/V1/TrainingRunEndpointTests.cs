namespace XE_Local_AI_Engine.Tests.Endpoints.Training.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the training run routes against the real DI host and an empty database. Every
///     route is Operator-gated, and the license confirmation is enforced at the boundary so an operator gets a 4xx
///     rather than a fault — the store enforces it a second time so no other caller can bypass it.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TrainingRunEndpointTests
{
    private const string ApiPrefix = "/api/local/v1/training/runs";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task EveryRunRoute_WithoutABearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var list = await client.GetAsync(ApiPrefix);
        using var byId = await client.GetAsync($"{ApiPrefix}/{Guid.NewGuid()}");
        using var defaults = await client.GetAsync($"{ApiPrefix}/defaults?baseArtifactId={Guid.NewGuid()}");
        using var create = new HttpRequestMessage(HttpMethod.Post, ApiPrefix);
        create.Headers.Add("Origin", "http://localhost");
        using var createResponse = await client.SendAsync(create);
        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/{Guid.NewGuid()}/cancel");
        cancel.Headers.Add("Origin", "http://localhost");
        using var cancelResponse = await client.SendAsync(cancel);

        AssertEx.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, byId.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, defaults.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, createResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.Unauthorized, cancelResponse.StatusCode);
    }

    [Test]
    public async Task ListRuns_WithOperatorToken_ReturnsAnEmptyPageOnACleanDatabase()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiPrefix);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.True(document.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() == 0,
            "An empty database is an empty page, never a 404.");
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Test]
    public async Task GetRun_WhenUnknown_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/{Guid.NewGuid()}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    ///     The by-id route's 200: the run the service hands back, mapped onto the wire. The encrypted freeze and
    ///     license documents the record carries must stay off the response — a detail view reads a run, it does not
    ///     republish the dataset.
    /// </summary>
    [Test]
    public async Task GetRun_WhenKnown_ReturnsTheRunWithoutItsServerSideDocuments()
    {
        var runId = Guid.NewGuid();
        var datasetId = Guid.NewGuid();
        var runs = Substitute.For<ITrainingRunService>();
        runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(new TrainingRunRecord
        {
            Id = runId,
            DatasetId = datasetId,
            DatasetContentFingerprint = "v1:abc",
            DatasetRevision = 3,
            FreezeJson = ReadOnlyMemory<byte>.Empty,
            BaseArtifactId = Guid.NewGuid(),
            LinkedInstalledModelName = "base:Q4_K_M",
            LinkedModelContentFingerprint = "v1:def",
            OptionsJson = ReadOnlyMemory<byte>.Empty,
            LicenseConfirmationJson = null,
            Status = TrainingRunStatus.Training,
            ProgressJson = null,
            LogTail = "step 1",
            LaunchReceiptJson = null,
            ErrorMessage = null,
            Version = 4,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 20,
            WorkStatus = TrainingWorkStatus.Running,
            WorkErrorMessage = null
        });
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITrainingRunService>();
                services.AddScoped(_ => runs);
            }
        };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/{runId}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(runId, document.RootElement.GetProperty("id").GetGuid());
        AssertEx.Equal(datasetId, document.RootElement.GetProperty("datasetId").GetGuid());
        AssertEx.Equal(expected: 3, document.RootElement.GetProperty("datasetRevision").GetInt32());
        AssertEx.Equal("Training", document.RootElement.GetProperty("status").GetString());
        AssertEx.Equal("base:Q4_K_M", document.RootElement.GetProperty("linkedInstalledModelName").GetString());
        AssertEx.Equal("step 1", document.RootElement.GetProperty("logTail").GetString());
        AssertEx.False(body.Contains("freeze", StringComparison.OrdinalIgnoreCase), "The dataset freeze stays server-side.");
        AssertEx.False(body.Contains("license", StringComparison.OrdinalIgnoreCase), "The license confirmation stays server-side.");
        await runs.Received(1).GetAsync(runId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelRun_WhenUnknown_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/{Guid.NewGuid()}/cancel");
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task RunCreate_WithoutLicenseConfirmation_Rejected()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiPrefix)
        {
            Content = JsonContent.Create(new
            {
                datasetId = Guid.NewGuid(),
                expectedDatasetVersion = 1,
                baseArtifactId = Guid.NewGuid(),
                licenseConfirmed = false
            })
        };
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        // The licensing gate is checked before anything else, so this is a 400 rather than the 400 an unknown dataset
        // would produce later — and nothing is queued either way, which the service suite pins against a real store.
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertEx.True(body.Contains("licens", StringComparison.OrdinalIgnoreCase), $"The refusal must name the licensing gate. Body: {body}");
    }

    [Test]
    public async Task GetRunDefaults_ForAnUnknownCheckpoint_IsRejectedRatherThanFaulting()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/defaults?baseArtifactId={Guid.NewGuid()}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task ListRuns_WithAnOutOfRangePageSize_IsRejected()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}?page=1&pageSize=5000");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
