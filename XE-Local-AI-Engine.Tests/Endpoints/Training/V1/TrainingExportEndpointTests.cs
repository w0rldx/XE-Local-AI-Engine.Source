namespace XE_Local_AI_Engine.Tests.Endpoints.Training.V1;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Export;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The export surface's HTTP contract: operator-only, a refusal that costs nothing, and the two conflicts an
///     operator can actually hit — a busy GPU and an artifact the registry already owns.
/// </summary>
public sealed class TrainingExportEndpointTests
{
    private const string Api = "/api/local/v1/training";
    private static readonly Guid RunId = new("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid ArtifactId = new("00000000-0000-0000-0000-0000000000e2");

    [Test]
    [Arguments("POST", "/runs/00000000-0000-0000-0000-0000000000e1/exports")]
    [Arguments("GET", "/runs/00000000-0000-0000-0000-0000000000e1/artifacts")]
    [Arguments("GET", "/artifacts/00000000-0000-0000-0000-0000000000e2")]
    [Arguments("DELETE", "/artifacts/00000000-0000-0000-0000-0000000000e2")]
    [Arguments("POST", "/artifacts/00000000-0000-0000-0000-0000000000e2/smoke")]
    [Arguments("POST", "/artifacts/00000000-0000-0000-0000-0000000000e2/promote")]
    public async Task EveryExportRoute_WithoutOperatorToken_ReturnsUnauthorized(string method, string path)
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), Api + path);
        if (method is "POST" or "DELETE")
        {
            request.Content = JsonContent.Create(new
            {
            });
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task StartExport_WhenAccepted_Returns202()
    {
        await using var context = new Context();
        _ = context.Exports.StartExportAsync(RunId, Arg.Any<TrainingExportRequest>(), Arg.Any<CancellationToken>())
                   .Returns(new TrainingExportStart(TrainingExportStartOutcome.Accepted));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/runs/{RunId}/exports", new
        {
            kind = "MergedGguf",
            quantType = "Q4_K_M"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        AssertEx.Contains(body, "\"quantType\":\"Q4_K_M\"", StringComparison.Ordinal);
    }

    [Test]
    public async Task StartExport_WhileTheGpuIsBusy_ReturnsConflict()
    {
        await using var context = new Context();
        _ = context.Exports.StartExportAsync(RunId, Arg.Any<TrainingExportRequest>(), Arg.Any<CancellationToken>())
                   .Returns(new TrainingExportStart(TrainingExportStartOutcome.Busy, "Training or another export is already running."));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/runs/{RunId}/exports", new
        {
            kind = "MergedGguf"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Contains(body, "Busy", StringComparison.Ordinal);
    }

    [Test]
    public async Task StartExport_WithAnUnsupportedQuantization_ReturnsBadRequest()
    {
        await using var context = new Context();
        _ = context.Exports.StartExportAsync(RunId, Arg.Any<TrainingExportRequest>(), Arg.Any<CancellationToken>())
                   .Returns(new TrainingExportStart(TrainingExportStartOutcome.UnsupportedQuantization, "not supported"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/runs/{RunId}/exports", new
        {
            kind = "MergedGguf",
            quantType = "IQ1_S"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task ListArtifacts_PublishesTheFileNameButNotTheStagedPath()
    {
        await using var context = new Context();
        _ = context.Store.ListArtifactsAsync(RunId, Arg.Any<CancellationToken>())
                   .Returns<IReadOnlyList<TrainingArtifactRecord>>([
                       new TrainingArtifactRecord(ArtifactId, RunId, TrainingArtifactKind.MergedGguf,
                           "/var/lib/xe/training/runs/x/staged/merged-Q4_K_M.gguf", "abc", 1024,
                           TrainingArtifactSmokeState.Passed, null, null, 2, 0, 0)
                   ]);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Get, $"{Api}/runs/{RunId}/artifacts");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"fileName\":\"merged-Q4_K_M.gguf\"", StringComparison.Ordinal);
        // The staged location is a server-side path; publishing it would leak the node's data-directory layout.
        AssertEx.False(body.Contains("/var/lib/xe", StringComparison.Ordinal), "The absolute staged path must stay server-side.");
    }

    /// <summary>
    ///     Delete goes through the export service, not the store: the store only removes the row, and the staged
    ///     bytes have to go with it. The conflict still surfaces as a 409 from wherever it is raised.
    /// </summary>
    [Test]
    public async Task DeleteArtifact_OncePromoted_ReturnsConflict()
    {
        await using var context = new Context();
        _ = context.Exports.DeleteArtifactAsync(ArtifactId, 2, Arg.Any<CancellationToken>())
                   .Returns<Task>(_ => throw new TrainingConflictException("ArtifactPromoted"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Delete, $"{Api}/artifacts/{ArtifactId}?expectedVersion=2");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Test]
    public async Task Promote_WhenTheArtifactIsRefused_ReturnsBadRequestWithTheReason()
    {
        await using var context = new Context();
        _ = context.Promotion.PromoteAsync(ArtifactId, "tuned", Arg.Any<CancellationToken>())
                   .Returns<Task<string>>(_ => throw new TrainingExportRejectedException("The artifact has not passed its smoke test."));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/promote", new
        {
            modelName = "tuned"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "smoke test", StringComparison.Ordinal);
    }

    [Test]
    public async Task Smoke_IsAcceptedWithoutARequestBody()
    {
        // The artifact id is the whole request and it comes from the route. Without the explicit Accepts declaration
        // FastEndpoints demands a JSON body and answers a bodyless re-run with 415.
        await using var context = new Context();
        _ = context.Exports.RunSmokeAsync(ArtifactId, Arg.Any<CancellationToken>())
                   .Returns(new TrainedModelSmokeResult(TrainingArtifactSmokeState.Passed, Reason: null));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/smoke");

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"smokeState\":\"Passed\"", StringComparison.Ordinal);
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

    private sealed class Context : IAsyncDisposable
    {
        public Context() =>
            Factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<ITrainingRunStore>();
                    services.RemoveAll<ITrainingExportService>();
                    services.RemoveAll<IArtifactPromotionService>();
                    _ = services.AddSingleton(Store);
                    _ = services.AddSingleton(Exports);
                    _ = services.AddSingleton(Promotion);
                }
            };

        public ITrainingRunStore Store { get; } = Substitute.For<ITrainingRunStore>();

        public ITrainingExportService Exports { get; } = Substitute.For<ITrainingExportService>();

        public IArtifactPromotionService Promotion { get; } = Substitute.For<IArtifactPromotionService>();

        public TestServerWebAppFactory Factory { get; }

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }
}
