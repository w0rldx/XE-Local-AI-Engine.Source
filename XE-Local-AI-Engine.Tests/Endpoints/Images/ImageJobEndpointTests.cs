namespace XE_Local_AI_Engine.Tests.Endpoints.Images;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the image-job API: every route requires the operator token (401 without it), a
///     create → get round-trip returns the persisted Queued view through a stubbed coordinator, and the body-less cancel
///     POST is accepted (not 415) — an unknown job reports 404.
/// </summary>
public sealed class ImageJobEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ImageEndpoints_RequireOperator()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var unauthorized = new (HttpMethod Method, string Route)[]
        {
            (HttpMethod.Get, $"{ApiPrefix}/images/jobs"),
            (HttpMethod.Post, $"{ApiPrefix}/images/jobs"),
            (HttpMethod.Get, $"{ApiPrefix}/images/jobs/{Guid.NewGuid()}"),
            (HttpMethod.Post, $"{ApiPrefix}/images/jobs/{Guid.NewGuid()}/cancel"),
            (HttpMethod.Get, $"{ApiPrefix}/images/{Guid.NewGuid()}"),
            (HttpMethod.Get, $"{ApiPrefix}/images/models"),
            (HttpMethod.Post, $"{ApiPrefix}/images/models/downloads")
        };

        foreach (var (method, route) in unauthorized)
        {
            using var request = new HttpRequestMessage(method, route);
            if (method != HttpMethod.Get)
            {
                request.Content = JsonContent.Create(new
                {
                });
            }

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
        }
    }

    [Test]
    public async Task CreateImageJob_ThenGet_RoundTrips()
    {
        var coordinator = new StubImageJobCoordinator();
        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageJobCoordinator>();
                services.AddSingleton<IImageJobCoordinator>(coordinator);
            }
        };
        using var client = factory.CreateClient();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/jobs")
        {
            Content = JsonContent.Create(new
            {
                modelName = "stable-diffusion-1.5",
                prompt = "a watercolor fox",
                steps = 20,
                width = 512,
                height = 512,
                cfgScale = 7.0
            })
        };
        factory.AddNodeBearerToken(createRequest);

        using var createResponse = await client.SendAsync(createRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        var jobId = created.GetProperty("id").GetGuid();
        AssertEx.NotEqual(Guid.Empty, jobId);
        AssertEx.Equal("Queued", created.GetProperty("status").GetString());
        AssertEx.Equal("a watercolor fox", created.GetProperty("prompt").GetString());

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/images/jobs/{jobId}");
        factory.AddNodeBearerToken(getRequest);
        using var getResponse = await client.SendAsync(getRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.Equal(jobId, fetched.GetProperty("id").GetGuid());
        AssertEx.Equal("stable-diffusion-1.5", fetched.GetProperty("modelName").GetString());
    }

    [Test]
    public async Task CancelImageJob_BodyLessPost_IsAcceptedNot415()
    {
        // Route-only POST binds the job id from the route, so a well-behaved client sends no body (no Content-Type). The
        // endpoint must accept that rather than 415; an unknown job then reports 404 (authorized + bound + dispatched).
        var coordinator = new StubImageJobCoordinator();
        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageJobCoordinator>();
                services.AddSingleton<IImageJobCoordinator>(coordinator);
            }
        };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/jobs/{Guid.NewGuid()}/cancel");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, "Body-less cancel POST must not return 415.");
        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "An unknown job on body-less cancel must report 404 (authorized + bound).");
    }

    // Deterministic in-memory coordinator: EnqueueAsync mints an id and stores a Queued view GetAsync then returns; no
    // sd-server, DbContext, or encryption is exercised. CancelAsync returns false for an unknown id (→ 404).
    private sealed class StubImageJobCoordinator : IImageJobCoordinator
    {
        private readonly ConcurrentDictionary<Guid, ImageJobView> _jobs = new();

        public Task<Guid> EnqueueAsync(CreateImageJobInput input, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(input);

            var id = Guid.NewGuid();
            _jobs[id] = new ImageJobView
            {
                Id = id,
                ModelName = input.ModelName,
                Prompt = input.Prompt,
                NegativePrompt = input.NegativePrompt,
                Seed = input.Seed,
                Width = input.Width,
                Height = input.Height,
                Steps = input.Steps,
                Sampler = input.Sampler ?? "euler_a",
                CfgScale = input.CfgScale,
                Status = ImageJobStatus.Queued,
                CreatedAtUtc = 0
            };
            return Task.FromResult(id);
        }

        public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult(_jobs.TryRemove(jobId, out _));

        public Task<ImageJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult(_jobs.TryGetValue(jobId, out var view) ? view : null);

        public Task<IReadOnlyList<ImageJobView>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ImageJobView>>([.. _jobs.Values]);

        public IReadOnlyList<ImageJobBufferedEvent> SnapshotBufferedEvents(Guid jobId) => [];
    }
}
