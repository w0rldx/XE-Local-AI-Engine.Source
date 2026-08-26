namespace XE_Local_AI_Engine.Tests.Endpoints.Images;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the two model-management routes an operator needs once a file-set download is
///     under way: cancelling an in-flight pull (an image model can be tens of gigabytes, so one that cannot be stopped
///     holds the node's bandwidth and disk until it finishes) and deleting an installed model to reclaim that disk.
///     Both require the operator token, and both are idempotent — cancelling a download that just finished, or deleting
///     a model that is not installed, is a success, because the operator clicking a stale row is a race, not a mistake.
/// </summary>
public sealed class ImageModelManagementEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task CancelAndDelete_RequireOperator()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/models/downloads/cancel")
        {
            Content = JsonContent.Create(new
            {
                modelName = "sd-1.5"
            })
        };
        using var cancelResponse = await client.SendAsync(cancel).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Unauthorized, cancelResponse.StatusCode, "Cancelling a download must require the operator token.");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{ApiPrefix}/images/models/sd-1.5");
        using var deleteResponse = await client.SendAsync(delete).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Unauthorized, deleteResponse.StatusCode, "Deleting a model must require the operator token.");
    }

    [Test]
    public async Task CancelDownload_WhenInFlight_ReportsCancelledAndReachesTheCoordinator()
    {
        var coordinator = new StubImageModelDownloadCoordinator
        {
            CancelResult = true
        };
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads/cancel", new
        {
            modelName = "sd-1.5"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.Equal("sd-1.5", body.GetProperty("modelName").GetString(), "The reply must echo the name so the caller can match it to the row it clicked.");
        AssertEx.True(body.GetProperty("cancelled").GetBoolean(), "An in-flight download that was signalled must report cancelled.");
        AssertEx.Equal("sd-1.5", coordinator.LastCancelledModelName);
    }

    [Test]
    public async Task CancelDownload_WhenNothingIsInFlight_IsStillASuccessReportingFalse()
    {
        var coordinator = new StubImageModelDownloadCoordinator
        {
            CancelResult = false
        };
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads/cancel", new
        {
            modelName = "already-finished"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "Cancelling a finished download is idempotent, not an error.");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.False(body.GetProperty("cancelled").GetBoolean(), "Nothing was in flight, so nothing may be claimed to have stopped.");
    }

    [Test]
    public async Task CancelDownload_WithABlankModelName_Returns400AndNeverReachesTheCoordinator()
    {
        var coordinator = new StubImageModelDownloadCoordinator();
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads/cancel", new
        {
            modelName = "   "
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Null(coordinator.LastCancelledModelName, "A blank name must be rejected at the boundary.");
    }

    [Test]
    public async Task DeleteModel_RemovesItThroughTheStore()
    {
        var store = new StubImageModelStore();
        await using var factory = FactoryWith(store);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/images/models/sd-1.5", body: null);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertEx.Equal("sd-1.5", store.LastDeletedModelName);
    }

    [Test]
    public async Task DeleteModel_WhenTheModelIsNotInstalled_IsStillANoContent()
    {
        // The store's own contract is idempotent (it removes the registry entry regardless), so a double-click on
        // Delete must not surface a 404 the operator would read as a failure.
        var store = new StubImageModelStore();
        await using var factory = FactoryWith(store);
        using var client = factory.CreateClient();

        using var first = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/images/models/never-installed", body: null);
        using var firstResponse = await client.SendAsync(first).ConfigureAwait(false);
        using var second = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/images/models/never-installed", body: null);
        using var secondResponse = await client.SendAsync(second).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
        AssertEx.Equal(HttpStatusCode.NoContent, secondResponse.StatusCode);
        AssertEx.Equal(expected: 2, store.DeleteCallCount);
    }

    [Test]
    public async Task DeleteModel_WithABlankModelName_Returns400AndNeverReachesTheStore()
    {
        var store = new StubImageModelStore();
        await using var factory = FactoryWith(store);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/images/models/%20%20", body: null);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, store.DeleteCallCount, "A whitespace-only route segment must be rejected before the store is touched.");
    }

    [Test]
    public async Task StartDownload_QwenImageFileSet_ReachesTheCoordinatorWithEveryPartFieldIntact()
    {
        // The whole Qwen-Image install rides on three things surviving the boundary: the QwenImage family and the Llm
        // role (both added after the original SD-only contract), the per-part repo override (a real Qwen set is split
        // across two repositories), and the declared sizes (without them the free-disk pre-flight silently no-ops and
        // no aggregate percentage can be computed for an 18 GB transfer).
        var coordinator = new RecordingImageModelDownloadCoordinator();
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads", new
        {
            modelName = "qwen-image",
            repoId = "QuantStack/Qwen-Image-GGUF",
            family = "QwenImage",
            parts = new object[]
            {
                new
                {
                    role = "Diffusion",
                    fileName = "Qwen_Image-Q4_K_M.gguf",
                    sizeBytes = 13_065_746_976L
                },
                new
                {
                    role = "Vae",
                    fileName = "VAE/Qwen_Image-VAE.safetensors",
                    sizeBytes = 253_806_246L
                },
                new
                {
                    role = "Llm",
                    fileName = "Qwen2.5-VL-7B-Instruct.Q4_K_M.gguf",
                    repoId = "mradermacher/Qwen2.5-VL-7B-Instruct-GGUF",
                    sizeBytes = 4_683_072_512L
                }
            }
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var started = coordinator.LastRequest;
        AssertEx.NotNull(started);
        AssertEx.Equal(ImageModelFamily.QwenImage, started!.Family);
        AssertEx.Equal(expected: 3, started.Parts.Count);

        var encoder = started.Parts.Single(part => part.Role == ImageModelPartRole.Llm);
        AssertEx.Equal("mradermacher/Qwen2.5-VL-7B-Instruct-GGUF", encoder.RepoId);
        AssertEx.Equal(expected: 4_683_072_512L, encoder.SizeBytes ?? 0);

        var vae = started.Parts.Single(part => part.Role == ImageModelPartRole.Vae);
        AssertEx.Null(vae.RepoId, "A part that named no repo must inherit the set's, not carry a copy of it.");
        AssertEx.Equal("VAE/Qwen_Image-VAE.safetensors", vae.FileName);
    }

    [Test]
    public async Task StartDownload_WithANonPositiveSize_TreatsItAsUndeclaredRatherThanZero()
    {
        // A zero would read as a declared size: it would disable the disk pre-flight it was meant to feed and poison the
        // set total, producing a bar that runs past 100%.
        var coordinator = new RecordingImageModelDownloadCoordinator();
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads", new
        {
            modelName = "sd-1.5",
            repoId = "second-state/stable-diffusion-v1-5-GGUF",
            family = "Sd15",
            parts = new object[]
            {
                new
                {
                    role = "Diffusion",
                    fileName = "weights.gguf",
                    sizeBytes = 0L
                }
            }
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        AssertEx.Null(coordinator.LastRequest?.Parts[0].SizeBytes);
    }

    [Test]
    public async Task StartDownload_NormalizesTextParsesEnumsCaseInsensitivelyAndDefaultsKindBeforeStarting()
    {
        var coordinator = new RecordingImageModelDownloadCoordinator();
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads", new
        {
            modelName = "  sd-1.5  ",
            repoId = "  second-state/stable-diffusion-v1-5-GGUF  ",
            family = "sD15",
            kind = "   ",
            revision = "  main  ",
            parts = new object[]
            {
                new
                {
                    role = "dIfFuSiOn",
                    fileName = "  weights.gguf  ",
                    sha256 = "  abc123  ",
                    repoId = "  override/repo  ",
                    sizeBytes = -1L
                }
            }
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = coordinator.LastRequest;
        AssertEx.NotNull(started);
        AssertEx.Equal("sd-1.5", started!.ModelName);
        AssertEx.Equal("second-state/stable-diffusion-v1-5-GGUF", started.RepoId);
        AssertEx.Equal(ImageModelFamily.Sd15, started.Family);
        AssertEx.Equal(ImageModelKind.Txt2Img, started.Kind);
        AssertEx.Equal("main", started.Revision);
        AssertEx.Equal(ImageModelPartRole.Diffusion, started.Parts[0].Role);
        AssertEx.Equal("weights.gguf", started.Parts[0].FileName);
        AssertEx.Equal("abc123", started.Parts[0].Sha256);
        AssertEx.Equal("override/repo", started.Parts[0].RepoId);
        AssertEx.Null(started.Parts[0].SizeBytes);
    }

    [Test]
    public async Task StartDownload_WhenCoordinatorRejoinsAnExistingDownload_ReturnsTheExactAcceptedBody()
    {
        var coordinator = new RecordingImageModelDownloadCoordinator
        {
            AlreadyInFlight = true
        };
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads", ValidStartPayload());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var body = document.RootElement;
        AssertEx.True(body.EnumerateObject()
                          .Select(static property => property.Name)
                          .SequenceEqual(["modelName", "accepted", "alreadyInFlight"], StringComparer.Ordinal),
            "The accepted response must retain its exact three-property wire schema and ordering.");
        AssertEx.Equal("sd-1.5", body.GetProperty("modelName").GetString());
        AssertEx.True(body.GetProperty("accepted").GetBoolean());
        AssertEx.True(body.GetProperty("alreadyInFlight").GetBoolean());
    }

    [Test]
    [Arguments("{\"modelName\":\" \",\"repoId\":\"repo/model\",\"family\":\"Sd15\",\"parts\":[{\"role\":\"Diffusion\",\"fileName\":\"weights.gguf\"}]}",
        "A model name is required.")]
    [Arguments("{\"modelName\":\"model\",\"repoId\":\" \",\"family\":\"Sd15\",\"parts\":[{\"role\":\"Diffusion\",\"fileName\":\"weights.gguf\"}]}",
        "A repository id is required.")]
    [Arguments("{\"modelName\":\"model\",\"repoId\":\"repo/model\",\"family\":\"Unknown\",\"parts\":[{\"role\":\"Diffusion\",\"fileName\":\"weights.gguf\"}]}",
        "A valid model family is required.")]
    [Arguments("{\"modelName\":\"model\",\"repoId\":\"repo/model\",\"family\":\"Sd15\",\"kind\":\"video\",\"parts\":[{\"role\":\"Diffusion\",\"fileName\":\"weights.gguf\"}]}",
        "The model kind is not recognized.")]
    [Arguments("{\"modelName\":\"model\",\"repoId\":\"repo/model\",\"family\":\"Sd15\",\"parts\":[]}",
        "At least one weight part is required.")]
    [Arguments("{\"modelName\":\"model\",\"repoId\":\"repo/model\",\"family\":\"Sd15\",\"parts\":[{\"role\":\"other\",\"fileName\":\"weights.gguf\"}]}",
        "The part role 'other' is not recognized.")]
    [Arguments("{\"modelName\":\"model\",\"repoId\":\"repo/model\",\"family\":\"Sd15\",\"parts\":[{\"role\":\"Diffusion\",\"fileName\":\" \"}]}",
        "Each weight part requires a file name.")]
    [Arguments("{\"modelName\":\"model\",\"repoId\":\"repo/model\",\"family\":\"Sd15\",\"parts\":[{\"role\":\"Vae\",\"fileName\":\"vae.safetensors\"}]}",
        "The file-set must include a diffusion part.")]
    public async Task StartDownload_WithInvalidWireInput_ReturnsTheExactGeneralErrorAndNeverStarts(string json, string expectedError)
    {
        var coordinator = new RecordingImageModelDownloadCoordinator();
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/models/downloads")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = document.RootElement.GetProperty("errors")[0];
        AssertEx.Equal("generalErrors", error.GetProperty("name").GetString());
        AssertEx.Equal(expectedError, error.GetProperty("reason").GetString());
        AssertEx.Null(coordinator.LastRequest);
    }

    [Test]
    public async Task StartDownload_WithTwoPartsClaimingTheSameRole_Returns400AndNeverStarts()
    {
        // The launch argument builder emits ONE flag per role and iterates the whole set, so a second VAE would pass
        // --vae twice and a second diffusion file would be downloaded and then never referenced. Easy to produce from
        // the repo file picker with two clicks, so it is rejected before a multi-gigabyte transfer starts rather than
        // surfacing later as a model the runtime cannot launch.
        var coordinator = new RecordingImageModelDownloadCoordinator();
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/images/models/downloads", new
        {
            modelName = "flux-broken",
            repoId = "second-state/FLUX.1-schnell-GGUF",
            family = "Flux",
            parts = new object[]
            {
                new
                {
                    role = "Diffusion",
                    fileName = "flux1-schnell-Q4_0.gguf",
                    sizeBytes = 6_688_845_536L
                },
                new
                {
                    role = "Diffusion",
                    fileName = "flux1-schnell-Q8_0.gguf",
                    sizeBytes = 12_634_000_000L
                }
            }
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var error = document.RootElement.GetProperty("errors")[0];
        AssertEx.Equal("generalErrors", error.GetProperty("name").GetString());
        AssertEx.Equal("The file-set declares the 'Diffusion' part more than once.", error.GetProperty("reason").GetString());
        AssertEx.Null(coordinator.LastRequest, "A duplicate role must be rejected at the boundary, before any transfer.");
    }

    private static TestServerWebAppFactory FactoryWith(IImageModelDownloadCoordinator coordinator)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageModelDownloadCoordinator>();
                services.AddSingleton(coordinator);
            }
        };
    }

    private static TestServerWebAppFactory FactoryWith(IImageModelStore store)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageModelStore>();
                services.AddSingleton(store);
            }
        };
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route, object? body)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        factory.AddNodeBearerToken(request);
        return request;
    }

    private static object ValidStartPayload() =>
        new
        {
            modelName = "sd-1.5",
            repoId = "second-state/stable-diffusion-v1-5-GGUF",
            family = "Sd15",
            parts = new object[]
            {
                new
                {
                    role = "Diffusion",
                    fileName = "weights.gguf"
                }
            }
        };

    // Records what the endpoint asked for; every other member is unreachable from these two routes.
    private sealed class StubImageModelDownloadCoordinator : IImageModelDownloadCoordinator
    {
        public bool CancelResult { get; init; }

        public string? LastCancelledModelName { get; private set; }

        public ImageModelDownloadTicket Start(ImageModelRequest request)
        {
            throw new NotSupportedException();
        }

        public bool Cancel(string modelName)
        {
            LastCancelledModelName = modelName;
            return CancelResult;
        }

        public ImageModelDownloadStatus? GetStatus(string modelName)
        {
            return null;
        }

        public IReadOnlyList<ImageModelDownloadStatus> ListStatuses()
        {
            return [];
        }
    }

    // Captures the request the start endpoint built, so the DTO-to-contract mapping can be asserted field by field.
    private sealed class RecordingImageModelDownloadCoordinator : IImageModelDownloadCoordinator
    {
        public bool AlreadyInFlight { get; init; }

        public ImageModelRequest? LastRequest { get; private set; }

        public ImageModelDownloadTicket Start(ImageModelRequest request)
        {
            LastRequest = request;
            return new ImageModelDownloadTicket(request.ModelName, AlreadyInFlight);
        }

        public bool Cancel(string modelName)
        {
            return false;
        }

        public ImageModelDownloadStatus? GetStatus(string modelName)
        {
            return null;
        }

        public IReadOnlyList<ImageModelDownloadStatus> ListStatuses()
        {
            return [];
        }
    }

    private sealed class StubImageModelStore : IImageModelStore
    {
        private readonly ConcurrentQueue<string> _deleted = new();

        public string? LastDeletedModelName => _deleted.LastOrDefault();

        public int DeleteCallCount => _deleted.Count;

        public Task<IReadOnlyList<ImageModelPart>?> ResolveModelPartsAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([]);
        }

        public Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task DeleteModelAsync(string modelName, CancellationToken ct)
        {
            _deleted.Enqueue(modelName);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult(result: false);
        }
    }
}
