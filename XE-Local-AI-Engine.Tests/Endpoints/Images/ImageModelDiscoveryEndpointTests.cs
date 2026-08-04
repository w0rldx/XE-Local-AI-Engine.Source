namespace XE_Local_AI_Engine.Tests.Endpoints.Images;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint tests for the three routes that replace hand-typing a model: the curated catalog, Hugging Face browse
///     and per-repo inspect. Two properties are load-bearing. Every one of them is operator-gated. And a Hub outage
///     must degrade to an empty list, never a 500 — the browse panel going red the moment Hugging Face hiccups is
///     exactly the failure the GGUF lane already learned to avoid.
/// </summary>
public sealed class ImageModelDiscoveryEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task CatalogBrowseAndInspect_RequireOperator()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        foreach (var route in new[]
                 {
                     "images/models/catalog", "images/models/browse", "images/models/inspect?repoId=o/r"
                 })
        {
            using var response = await client.GetAsync(new Uri($"{ApiPrefix}/{route}", UriKind.Relative)).ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{route} must require the operator token.");
        }
    }

    [Test]
    public async Task Catalog_ReturnsTheBundledEntries_WithAFitVerdictAndAnInstalledFlag()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "images/models/catalog");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        AssertEx.NotEmpty(items, "The bundled catalog must reach the wire; an empty list means the embedded seed failed to load.");

        var sd15 = items.Single(item => item.GetProperty("id").GetString() == "sd-1.5");
        AssertEx.Equal("second-state/stable-diffusion-v1-5-GGUF", sd15.GetProperty("repoId").GetString());
        AssertEx.True(sd15.GetProperty("totalSizeBytes").GetInt64() > 0, "The total is what the free-disk pre-flight and the progress bar are computed from.");
        AssertEx.False(sd15.GetProperty("isInstalled").GetBoolean(), "Nothing is installed in a fresh test node.");

        // Unknown is a legitimate verdict on a box whose VRAM could not be probed — the test host is one such box, so
        // this asserts the value is one of the four, not which one.
        var verdict = sd15.GetProperty("fitVerdict").GetString();
        AssertEx.True(verdict is "Fits" or "Tight" or "WontFit" or "Unknown", $"Unexpected fit verdict '{verdict}'.");

        // The parts array is the whole point: it is posted back to images/models/downloads unchanged.
        var parts = sd15.GetProperty("parts").EnumerateArray().ToList();
        AssertEx.Equal(expected: 1, parts.Count);
        AssertEx.Equal("Diffusion", parts[0].GetProperty("role").GetString());
        AssertEx.True(parts[0].GetProperty("sizeBytes").GetInt64() > 0);
    }

    [Test]
    public async Task Catalog_MarksAnEntryInstalled_WhenTheRegistryHoldsAModelOfThatName()
    {
        // The catalog id doubles as the installed model name, which is what lets a row render "Installed" instead of
        // offering a second download of weights already on disk.
        await using var factory = FactoryWithDiscovery(new StubImageModelDiscovery(), new StubImageModelRegistry("sd-1.5"));
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "images/models/catalog");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        AssertEx.True(items.Single(item => item.GetProperty("id").GetString() == "sd-1.5").GetProperty("isInstalled").GetBoolean());
        AssertEx.False(items.Single(item => item.GetProperty("id").GetString() == "qwen-image").GetProperty("isInstalled").GetBoolean());
    }

    [Test]
    public async Task Browse_PassesTheQueryThrough_AndProjectsTheSummaries()
    {
        var discovery = new StubImageModelDiscovery
        {
            Summaries =
            [
                new ImageRepoSummary("QuantStack/Qwen-Image-GGUF",
                    IsGated: false,
                    Downloads: 4321,
                    Likes: 21,
                    new DateTimeOffset(year: 2026, month: 7, day: 1, hour: 0, minute: 0, second: 0, TimeSpan.Zero),
                    "apache-2.0",
                    HasUsableWeights: true,
                    IsTrustedPublisher: false)
            ]
        };
        await using var factory = FactoryWithDiscovery(discovery, registry: null);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "images/models/browse?query=qwen&limit=5&sort=downloads&ggufOnly=true");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("qwen", discovery.LastQuery?.SearchText);
        AssertEx.Equal(expected: 5, discovery.LastQuery?.Limit ?? 0);
        AssertEx.Equal(ImageModelSearchSort.Downloads, discovery.LastQuery?.Sort);
        AssertEx.True(discovery.LastQuery?.GgufOnly ?? false);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        var row = body.GetProperty("items").EnumerateArray().Single();
        AssertEx.Equal("QuantStack/Qwen-Image-GGUF", row.GetProperty("repoId").GetString());
        AssertEx.False(row.GetProperty("isTrustedPublisher").GetBoolean(), "The unverified-publisher badge is a soft warning the UI renders; it must survive the wire.");
        AssertEx.True(row.GetProperty("lastModifiedAtUtc").GetInt64() > 0);
    }

    [Test]
    public async Task Browse_WhenTheHubIsUnreachable_Returns200WithAnEmptyList()
    {
        var discovery = new StubImageModelDiscovery
        {
            Failure = () => new HttpRequestException("no network")
        };
        await using var factory = FactoryWithDiscovery(discovery, registry: null);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "images/models/browse?query=qwen");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "A Hub outage must not turn the browse panel red.");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, body.GetProperty("items").GetArrayLength());
    }

    [Test]
    public async Task Inspect_ProjectsFilesWithTheirSuggestedRole()
    {
        var discovery = new StubImageModelDiscovery
        {
            Detail = new ImageRepoDetail("second-state/FLUX.1-schnell-GGUF",
                IsGated: false,
                "apache-2.0",
                [
                    new ImageRepoFile("flux1-schnell-Q4_0.gguf", ImageWeightFormat.Gguf, SizeBytes: 6_688_845_536L, "aa", ImageModelPartRole.Diffusion),
                    new ImageRepoFile("ae.safetensors", ImageWeightFormat.Safetensors, SizeBytes: 335_304_388L, Sha256: null, ImageModelPartRole.Vae)
                ])
        };
        await using var factory = FactoryWithDiscovery(discovery, registry: null);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "images/models/inspect?repoId=second-state/FLUX.1-schnell-GGUF");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        var files = body.GetProperty("files").EnumerateArray().ToList();
        AssertEx.Equal(expected: 2, files.Count);
        AssertEx.Equal("Vae", files.Single(f => f.GetProperty("fileName").GetString() == "ae.safetensors").GetProperty("suggestedRole").GetString());
        AssertEx.Equal("Safetensors", files.Single(f => f.GetProperty("fileName").GetString() == "ae.safetensors").GetProperty("format").GetString());
    }

    [Test]
    public async Task Inspect_WithoutARepoId_Returns400()
    {
        var discovery = new StubImageModelDiscovery();
        await using var factory = FactoryWithDiscovery(discovery, registry: null);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "images/models/inspect?repoId=%20");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Null(discovery.LastInspectedRepoId, "A blank repo id must be rejected before discovery is touched.");
    }

    [Test]
    public async Task Inspect_WhenTheHubIsUnreachable_Returns200WithAnEmptyFileList()
    {
        var discovery = new StubImageModelDiscovery
        {
            Failure = () => new TimeoutException("hub timeout")
        };
        await using var factory = FactoryWithDiscovery(discovery, registry: null);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, "images/models/inspect?repoId=owner/repo");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, body.GetProperty("files").GetArrayLength());
    }

    private static TestingWebAppFactory FactoryWithDiscovery(IImageModelDiscovery discovery, IImageModelRegistry? registry)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageModelDiscovery>();
                services.AddSingleton(discovery);
                if (registry is not null)
                {
                    services.RemoveAll<IImageModelRegistry>();
                    services.AddSingleton(registry);
                }
            }
        };
    }

    private static HttpRequestMessage Authorized(TestingWebAppFactory factory, string route)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/{route}");
        factory.AddNodeBearerToken(request);
        return request;
    }

    private sealed class StubImageModelDiscovery : IImageModelDiscovery
    {
        public IReadOnlyList<ImageRepoSummary> Summaries { get; init; } = [];

        public ImageRepoDetail Detail { get; init; } = new("owner/repo", IsGated: false, License: null, []);

        public Func<Exception>? Failure { get; init; }

        public ImageModelSearchQuery? LastQuery { get; private set; }

        public string? LastInspectedRepoId { get; private set; }

        public Task<IReadOnlyList<ImageRepoSummary>> SearchAsync(ImageModelSearchQuery query, CancellationToken ct)
        {
            LastQuery = query;
            return Failure is null ? Task.FromResult(Summaries) : Task.FromException<IReadOnlyList<ImageRepoSummary>>(Failure());
        }

        public Task<ImageRepoDetail> InspectRepoAsync(string repoId, CancellationToken ct)
        {
            LastInspectedRepoId = repoId;
            return Failure is null ? Task.FromResult(Detail) : Task.FromException<ImageRepoDetail>(Failure());
        }
    }

    private sealed class StubImageModelRegistry(params string[] installedModelNames) : IImageModelRegistry
    {
        private readonly IReadOnlyList<ImageModelRegistryEntry> _entries =
        [
            .. installedModelNames.Select(static name => new ImageModelRegistryEntry
            {
                ModelName = name,
                RepoId = "owner/repo",
                Family = ImageModelFamily.Sd15,
                Kind = ImageModelKind.Txt2Img,
                Parts = [],
                SizeBytes = 1,
                SourceRevision = "main",
                DownloadedAtUtc = DateTimeOffset.UnixEpoch
            })
        ];

        public Task<IReadOnlyList<ImageModelRegistryEntry>> ListAsync(CancellationToken ct)
        {
            return Task.FromResult(_entries);
        }

        public Task<ImageModelRegistryEntry?> FindAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult(_entries.FirstOrDefault(entry => string.Equals(entry.ModelName, modelName, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
