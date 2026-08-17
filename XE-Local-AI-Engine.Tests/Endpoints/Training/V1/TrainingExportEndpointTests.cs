namespace XE_Local_AI_Engine.Tests.Endpoints.Training.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
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
    [Arguments("PUT", "/artifacts/00000000-0000-0000-0000-0000000000e2/quality")]
    [Arguments("POST", "/artifacts/00000000-0000-0000-0000-0000000000e2/quality/revalidation")]
    [Arguments("POST", "/artifacts/00000000-0000-0000-0000-0000000000e2/quality/override")]
    [Arguments("POST", "/artifacts/00000000-0000-0000-0000-0000000000e2/quality/discard")]
    public async Task EveryExportRoute_WithoutOperatorToken_ReturnsUnauthorized(string method, string path)
    {
        await using var context = new Context();
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

    [Test]
    public async Task DecideQuality_BindsRouteArtifactAndBodyComparisonVersion()
    {
        await using var context = new Context();
        var comparisonId = Guid.NewGuid();
        _ = context.Quality.DecideAsync(ArtifactId, comparisonId, 7, Arg.Any<CancellationToken>())
                   .Returns(DecidedArtifact(ArtifactQualityOutcome.Passed, comparisonId, version: 8));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, $"{Api}/artifacts/{ArtifactId}/quality", new
        {
            comparisonId,
            expectedVersion = 7
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = await context.Quality.Received(1).DecideAsync(ArtifactId, comparisonId, 7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DecideQuality_OnVersionConflict_ReturnsConflict()
    {
        await using var context = new Context();
        _ = context.Quality.DecideAsync(ArtifactId, Arg.Any<Guid>(), 6, Arg.Any<CancellationToken>())
                   .Returns<Task<TrainingArtifactRecord>>(_ => throw new TrainingConflictException("VersionConflict"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Put, $"{Api}/artifacts/{ArtifactId}/quality", new
        {
            comparisonId = Guid.NewGuid(),
            expectedVersion = 6
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Test]
    public async Task BeginQualityRevalidation_BindsRouteArtifactAndExpectedVersion()
    {
        await using var context = new Context();
        var comparisonId = Guid.NewGuid();
        _ = context.Quality.BeginRevalidationAsync(ArtifactId, 7, Arg.Any<CancellationToken>())
                   .Returns(DecidedArtifact(ArtifactQualityOutcome.Pending, comparisonId, version: 8));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post,
            $"{Api}/artifacts/{ArtifactId}/quality/revalidation", new { expectedVersion = 7 });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"outcome\":\"Pending\"", StringComparison.Ordinal);
        _ = await context.Quality.Received(1).BeginRevalidationAsync(ArtifactId, 7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BeginQualityRevalidation_OnVersionConflict_ReturnsConflict()
    {
        await using var context = new Context();
        _ = context.Quality.BeginRevalidationAsync(ArtifactId, 7, Arg.Any<CancellationToken>())
                   .Returns<Task<TrainingArtifactRecord>>(_ => throw new TrainingConflictException("VersionConflict"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post,
            $"{Api}/artifacts/{ArtifactId}/quality/revalidation", new { expectedVersion = 7 });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Test]
    [Arguments("quality", true)]
    [Arguments("quality/revalidation", false)]
    [Arguments("quality/override", false)]
    [Arguments("quality/discard", false)]
    public async Task QualityMutation_WithoutExpectedVersion_IsRejectedBeforeService(string suffix, bool includeComparison)
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory,
            includeComparison ? HttpMethod.Put : HttpMethod.Post,
            $"{Api}/artifacts/{ArtifactId}/{suffix}",
            includeComparison
                ? new { comparisonId = Guid.NewGuid(), reason = "" }
                : new { comparisonId = Guid.Empty, reason = "audited reason" });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _ = await context.Quality.DidNotReceiveWithAnyArgs()
                                 .DecideAsync(Guid.Empty, Guid.Empty, 0, CancellationToken.None);
        _ = await context.Quality.DidNotReceiveWithAnyArgs()
                                 .OverrideAsync(Guid.Empty, 0, string.Empty, CancellationToken.None);
        _ = await context.Quality.DidNotReceiveWithAnyArgs()
                                 .BeginRevalidationAsync(Guid.Empty, 0, CancellationToken.None);
        _ = await context.Exports.DidNotReceiveWithAnyArgs()
                         .DiscardArtifactQualityAsync(Guid.Empty, 0, string.Empty, CancellationToken.None);
    }

    [Test]
    public async Task OverrideQuality_BlankReason_IsRejectedBeforeService()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/quality/override", new
        {
            expectedVersion = 7,
            reason = "  "
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _ = await context.Quality.DidNotReceiveWithAnyArgs().OverrideAsync(Guid.Empty, 0, string.Empty, CancellationToken.None);
    }

    [Test]
    public async Task OverrideQuality_ReasonAboveAuditLimit_IsRejectedBeforeService()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/quality/override", new
        {
            expectedVersion = 7,
            reason = new string('x', count: 1025)
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _ = await context.Quality.DidNotReceiveWithAnyArgs().OverrideAsync(Guid.Empty, 0, string.Empty, CancellationToken.None);
    }

    [Test]
    public async Task OverrideQuality_BindsRouteVersionAndAuditedReason()
    {
        await using var context = new Context();
        var comparisonId = Guid.NewGuid();
        _ = context.Quality.OverrideAsync(ArtifactId, 7, "accepted regression", Arg.Any<CancellationToken>())
                   .Returns(DecidedArtifact(ArtifactQualityOutcome.Overridden, comparisonId, version: 8));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/quality/override", new
        {
            expectedVersion = 7,
            reason = "accepted regression"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = await context.Quality.Received(1).OverrideAsync(ArtifactId, 7, "accepted regression", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OverrideQuality_OnVersionConflict_ReturnsConflict()
    {
        await using var context = new Context();
        _ = context.Quality.OverrideAsync(ArtifactId, 7, "accepted regression", Arg.Any<CancellationToken>())
                   .Returns<Task<TrainingArtifactRecord>>(_ => throw new TrainingConflictException("VersionConflict"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/quality/override", new
        {
            expectedVersion = 7,
            reason = "accepted regression"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Test]
    public async Task DiscardQuality_BindsRouteVersionReasonAndReturnsTombstone()
    {
        await using var context = new Context();
        var comparisonId = Guid.NewGuid();
        var discarded = DecidedArtifact(ArtifactQualityOutcome.Failed, comparisonId, version: 8) with
        {
            QualityComparisonId = null,
            DiscardedAtUtc = 123,
            DiscardReason = "failed quality"
        };
        _ = context.Exports.DiscardArtifactQualityAsync(ArtifactId, 7, "failed quality", Arg.Any<CancellationToken>()).Returns(discarded);
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/quality/discard", new
        {
            expectedVersion = 7,
            reason = "failed quality"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Contains(body, "\"discardedAtUtc\":123", StringComparison.Ordinal);
        _ = await context.Exports.Received(1).DiscardArtifactQualityAsync(ArtifactId, 7, "failed quality", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DiscardQuality_BlankReason_IsRejectedBeforeService()
    {
        await using var context = new Context();
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/quality/discard", new
        {
            expectedVersion = 7,
            reason = ""
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _ = await context.Exports.DidNotReceiveWithAnyArgs()
                         .DiscardArtifactQualityAsync(Guid.Empty, 0, string.Empty, CancellationToken.None);
    }

    [Test]
    public async Task DiscardQuality_OnVersionConflict_ReturnsConflict()
    {
        await using var context = new Context();
        _ = context.Exports.DiscardArtifactQualityAsync(ArtifactId, 7, "failed quality", Arg.Any<CancellationToken>())
                   .Returns<Task<TrainingArtifactRecord>>(_ => throw new TrainingConflictException("VersionConflict"));
        using var client = context.Factory.CreateClient();
        using var request = Authorized(context.Factory, HttpMethod.Post, $"{Api}/artifacts/{ArtifactId}/quality/discard", new
        {
            expectedVersion = 7,
            reason = "failed quality"
        });

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static TrainingArtifactRecord DecidedArtifact(ArtifactQualityOutcome outcome, Guid comparisonId, long version)
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var decision = new ArtifactQualityDecisionV1
        {
            ArtifactId = ArtifactId,
            ArtifactSha256 = sha,
            ComparisonId = comparisonId,
            Outcome = outcome,
            FailureCodes = outcome == ArtifactQualityOutcome.Passed ? [] : ["AggregateRegression"],
            OverrideReason = outcome == ArtifactQualityOutcome.Overridden ? "accepted regression" : null,
            OverriddenAtUtc = outcome == ArtifactQualityOutcome.Overridden ? 123 : null
        };
        return new TrainingArtifactRecord(ArtifactId, RunId, TrainingArtifactKind.MergedGguf, "staged.gguf", sha, 4,
            TrainingArtifactSmokeState.Passed, null, null, version, 0, 0, comparisonId,
            JsonSerializer.SerializeToUtf8Bytes(decision, TrainingJson.Options));
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
                    services.RemoveAll<IArtifactQualityService>();
                    _ = services.AddSingleton(Store);
                    _ = services.AddSingleton(Exports);
                    _ = services.AddSingleton(Promotion);
                    _ = services.AddSingleton(Quality);
                }
            };

        public ITrainingRunStore Store { get; } = Substitute.For<ITrainingRunStore>();

        public ITrainingExportService Exports { get; } = Substitute.For<ITrainingExportService>();

        public IArtifactPromotionService Promotion { get; } = Substitute.For<IArtifactPromotionService>();

        public IArtifactQualityService Quality { get; } = Substitute.For<IArtifactQualityService>();

        public TestServerWebAppFactory Factory { get; }

        public ValueTask DisposeAsync() =>
            Factory.DisposeAsync();
    }
}
