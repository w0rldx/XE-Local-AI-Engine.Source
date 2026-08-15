namespace XE_Local_AI_Engine.Tests.Endpoints.Training.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class TrainingEndpointTests
{
    private const string Api = "/api/local/v1/training";
    private static readonly Guid DefinitionId = new("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid DatasetId = new("00000000-0000-0000-0000-0000000000d2");
    private static readonly Guid MockId = new("00000000-0000-0000-0000-0000000000d3");

    [Test]
    [Arguments("GET", "/definitions")]
    [Arguments("POST", "/definitions")]
    [Arguments("GET", "/definitions/00000000-0000-0000-0000-0000000000d1")]
    [Arguments("PUT", "/definitions/00000000-0000-0000-0000-0000000000d1")]
    [Arguments("DELETE", "/definitions/00000000-0000-0000-0000-0000000000d1")]
    [Arguments("POST", "/definitions/00000000-0000-0000-0000-0000000000d1/generate")]
    [Arguments("GET", "/datasets")]
    [Arguments("GET", "/datasets/00000000-0000-0000-0000-0000000000d2")]
    [Arguments("DELETE", "/datasets/00000000-0000-0000-0000-0000000000d2")]
    [Arguments("GET", "/datasets/00000000-0000-0000-0000-0000000000d2/samples")]
    [Arguments("PATCH", "/datasets/00000000-0000-0000-0000-0000000000d2/samples/00000000-0000-0000-0000-0000000000d4")]
    [Arguments("GET", "/datasets/00000000-0000-0000-0000-0000000000d2/export")]
    [Arguments("GET", "/mocks")]
    [Arguments("POST", "/mocks")]
    [Arguments("GET", "/mocks/00000000-0000-0000-0000-0000000000d3")]
    [Arguments("PUT", "/mocks/00000000-0000-0000-0000-0000000000d3")]
    [Arguments("DELETE", "/mocks/00000000-0000-0000-0000-0000000000d3")]
    [Arguments("POST", "/mocks/00000000-0000-0000-0000-0000000000d3/verify")]
    public async Task EveryTrainingRoute_WithoutOperatorToken_ReturnsUnauthorized(string method, string path)
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), Api + path);
        if (method is "POST" or "PUT" or "DELETE" or "PATCH")
        {
            request.Content = JsonContent.Create(new
            {
            });
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Generate_WhileATrainingRunIsActive_ReturnsConflict()
    {
        await using var context = new Context();
        _ = context.Generation.StartAsync(DefinitionId, Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns<Task<TrainingDatasetRecord>>(_ => throw new TrainingConflictException("TrainingBusy"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/definitions/{DefinitionId}/generate", new
        {
            expectedVersion = 1,
            name = "dataset"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Contains(body, "TrainingBusy", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task Generate_WhenAccepted_Returns202WithTheQueuedDataset()
    {
        await using var context = new Context();
        _ = context.Generation.StartAsync(DefinitionId, 3, "dataset", Arg.Any<CancellationToken>()).Returns(Dataset());
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/definitions/{DefinitionId}/generate", new
        {
            expectedVersion = 3,
            name = "dataset"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        AssertEx.Contains(body, "\"status\":\"Generating\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task DeleteDataset_WhileGenerationIsActive_ReturnsConflict()
    {
        await using var context = new Context();
        _ = context.Store.DeleteDatasetAsync(DatasetId, 4, Arg.Any<CancellationToken>())
                   .Returns<Task>(_ => throw new TrainingConflictException("GenerationActive"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Delete, $"{Api}/datasets/{DatasetId}?expectedVersion=4");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Contains(body, "GenerationActive", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task ListSamples_RejectsAnOutOfRangePageSize()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, $"{Api}/datasets/{DatasetId}/samples?page=1&pageSize=500");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Export_DefaultsToTheCanonicalJsonlFormat()
    {
        await using var context = new Context();
        _ = context.Export.ExportAsync(DatasetId, DatasetExportFormat.Jsonl, Arg.Any<CancellationToken>()).Returns("{\"sequence\":0}\n");
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, $"{Api}/datasets/{DatasetId}/export");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"format\":\"Jsonl\"", StringComparison.Ordinal);
        AssertEx.Contains(body, "\"lineCount\":1", StringComparison.Ordinal);
    }

    [Test]
    public async Task VerifyMock_ReturnsTheRecordedVerdict()
    {
        await using var context = new Context();
        var verified = Mock(ToolMockVerificationState.Verified);
        _ = context.Mocks.VerifyAsync(MockId, 2, Arg.Any<CancellationToken>())
                   .Returns(new ToolMockVerifyResult(verified, new ToolMockVerificationV1(1, Passed: true, [])));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/mocks/{MockId}/verify", new
        {
            expectedVersion = 2
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"verificationState\":\"Verified\"", StringComparison.Ordinal);
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

    private static TrainingDatasetRecord Dataset() =>
        new(DatasetId, DefinitionId, 3, Encoding.UTF8.GetBytes("""{"schemaVersion":1,"teacherModelName":"teacher.gguf"}"""),
            "dataset", TrainingDatasetStatus.Generating, 1, null, 0, 0, 0, 0, 0, 1, 0, 0,
            DatasetGenerationWorkStatus.Queued, null);

    private static ToolMockRecord Mock(ToolMockVerificationState state) =>
        new(MockId, "read_file", Encoding.UTF8.GetBytes("""{"schemaVersion":1,"rules":[]}"""), null, state, Enabled: true,
            Version: 3, CreatedAtUtc: 0, UpdatedAtUtc: 0);

    private sealed class Context : IAsyncDisposable
    {
        public Context() =>
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<ITrainingDatasetStore>();
                    services.RemoveAll<IDatasetDefinitionService>();
                    services.RemoveAll<IDatasetGenerationService>();
                    services.RemoveAll<IDatasetExportService>();
                    services.RemoveAll<IToolMockService>();
                    services.AddSingleton(Store);
                    services.AddSingleton(Definitions);
                    services.AddSingleton(Generation);
                    services.AddSingleton(Export);
                    services.AddSingleton(Mocks);
                }
            };

        public ITrainingDatasetStore Store { get; } = Substitute.For<ITrainingDatasetStore>();

        public IDatasetDefinitionService Definitions { get; } = Substitute.For<IDatasetDefinitionService>();

        public IDatasetGenerationService Generation { get; } = Substitute.For<IDatasetGenerationService>();

        public IDatasetExportService Export { get; } = Substitute.For<IDatasetExportService>();

        public IToolMockService Mocks { get; } = Substitute.For<IToolMockService>();

        public TestServerWebAppFactory Factory { get; }

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }
}
