namespace XE_Local_AI_Engine.Tests.Endpoints.Images;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the image-job API: every route requires the operator token (401 without it), a
///     create → get round-trip returns the persisted Queued view through a stubbed coordinator, and the body-less cancel
///     POST is accepted (not 415) — an unknown job reports 404. Delete answers 204 for a terminal job, 404 for an
///     unknown one and 409 while the job is still in play; the list is paged server-side and carries the unpaged total.
///     Edit parameters are validated, carried to the coordinator and echoed; models carry their family's edit modes.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ImageJobEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ImageEndpoints_RequireOperator()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        var unauthorized = new (HttpMethod Method, string Route)[]
        {
            (HttpMethod.Get, $"{ApiPrefix}/images/jobs"),
            (HttpMethod.Post, $"{ApiPrefix}/images/jobs"),
            (HttpMethod.Get, $"{ApiPrefix}/images/jobs/{Guid.NewGuid()}"),
            (HttpMethod.Post, $"{ApiPrefix}/images/jobs/{Guid.NewGuid()}/cancel"),
            (HttpMethod.Delete, $"{ApiPrefix}/images/jobs/{Guid.NewGuid()}"),
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

            using var response = await client.SendAsync(request);
            AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
        }
    }

    [Test]
    public async Task CreateImageJob_ThenGet_RoundTrips()
    {
        var coordinator = new StubImageJobCoordinator();
        await using var factory = new TestServerWebAppFactory
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

        using var createResponse = await client.SendAsync(createRequest);
        AssertEx.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var jobId = created.GetProperty("id").GetGuid();
        AssertEx.NotEqual(Guid.Empty, jobId);
        AssertEx.Equal("Queued", created.GetProperty("status").GetString());
        AssertEx.Equal("a watercolor fox", created.GetProperty("prompt").GetString());

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/images/jobs/{jobId}");
        factory.AddNodeBearerToken(getRequest);
        using var getResponse = await client.SendAsync(getRequest);

        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal(jobId, fetched.GetProperty("id").GetGuid());
        AssertEx.Equal("stable-diffusion-1.5", fetched.GetProperty("modelName").GetString());
    }

    [Test]
    public async Task CreateImageJob_WithLargeSeed_RoundTripsExactlyAsString()
    {
        // Blocker 3: a 64-bit seed above 2^53 (9007199254740992) must survive the wire exactly. Carried as a JSON string,
        // it reaches the coordinator as the exact long and comes back as the exact string — a JSON number would round it.
        const string largeSeed = "9007199254740993";
        var coordinator = new StubImageJobCoordinator();
        await using var factory = new TestServerWebAppFactory
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
                seed = largeSeed
            })
        };
        factory.AddNodeBearerToken(createRequest);

        using var createResponse = await client.SendAsync(createRequest);
        AssertEx.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        // The response seed is a JSON string equal to the exact value sent — never a rounded number.
        AssertEx.Equal(JsonValueKind.String, created.GetProperty("seed").ValueKind);
        AssertEx.Equal(largeSeed, created.GetProperty("seed").GetString());
        // The coordinator received the exact long, proving no precision was lost crossing the DTO boundary.
        AssertEx.Equal(9007199254740993L, coordinator.LastInput!.Seed);
    }

    [Test]
    public async Task CreateImageJob_WithBlankSeed_DefaultsToRandomSentinel()
    {
        // A null/omitted seed requests a runtime-chosen seed — the coordinator's -1 sentinel.
        var coordinator = new StubImageJobCoordinator();
        await using var factory = new TestServerWebAppFactory
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
                prompt = "a watercolor fox"
            })
        };
        factory.AddNodeBearerToken(createRequest);

        using var createResponse = await client.SendAsync(createRequest);

        AssertEx.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        AssertEx.Equal(expected: -1L, coordinator.LastInput!.Seed);
    }

    [Test]
    public async Task CreateImageJob_WithNonIntegerSeed_Returns400()
    {
        var coordinator = new StubImageJobCoordinator();
        await using var factory = new TestServerWebAppFactory
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
                seed = "not-a-number"
            })
        };
        factory.AddNodeBearerToken(createRequest);

        using var createResponse = await client.SendAsync(createRequest);

        AssertEx.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
        AssertEx.Null(coordinator.LastInput);
    }

    [Test]
    public async Task CancelImageJob_BodyLessPost_IsAcceptedNot415()
    {
        // Route-only POST binds the job id from the route, so a well-behaved client sends no body (no Content-Type). The
        // endpoint must accept that rather than 415; an unknown job then reports 404 (authorized + bound + dispatched).
        var coordinator = new StubImageJobCoordinator();
        await using var factory = new TestServerWebAppFactory
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

        using var response = await client.SendAsync(request);

        AssertEx.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, "Body-less cancel POST must not return 415.");
        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "An unknown job on body-less cancel must report 404 (authorized + bound).");
    }

    [Test]
    public async Task DeleteImageJob_WhenTerminal_Returns204AndRemovesTheJob()
    {
        var coordinator = new StubImageJobCoordinator();
        await using var factory = NewFactory(coordinator);
        using var client = factory.CreateClient();

        var jobId = await CreateJobAsync(factory, client);
        coordinator.Terminalize(jobId);

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{ApiPrefix}/images/jobs/{jobId}");
        factory.AddNodeBearerToken(deleteRequest);
        using var deleteResponse = await client.SendAsync(deleteRequest);
        AssertEx.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/images/jobs/{jobId}");
        factory.AddNodeBearerToken(getRequest);
        using var getResponse = await client.SendAsync(getRequest);
        AssertEx.Equal(HttpStatusCode.NotFound, getResponse.StatusCode, "A deleted job must no longer resolve.");
    }

    [Test]
    public async Task DeleteImageJob_WhenUnknown_Returns404()
    {
        await using var factory = NewFactory(new StubImageJobCoordinator());
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiPrefix}/images/jobs/{Guid.NewGuid()}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task DeleteImageJob_WhileQueued_Returns409WithTheOutcomeCode()
    {
        // The node refuses to delete a job it is still working on rather than cancelling it on the operator's behalf,
        // and the reason rides as a machine-readable `outcome` member so the SPA need not match on the message.
        var coordinator = new StubImageJobCoordinator();
        await using var factory = NewFactory(coordinator);
        using var client = factory.CreateClient();

        var jobId = await CreateJobAsync(factory, client);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiPrefix}/images/jobs/{jobId}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal("NotTerminal", problem.GetProperty("outcome").GetString());

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/images/jobs/{jobId}");
        factory.AddNodeBearerToken(getRequest);
        using var getResponse = await client.SendAsync(getRequest);
        AssertEx.Equal(HttpStatusCode.OK, getResponse.StatusCode, "A refused delete must leave the job in place.");
    }

    [Test]
    public async Task ListImageJobs_HonoursLimitAndOffsetAndReportsTheUnpagedTotal()
    {
        var coordinator = new StubImageJobCoordinator();
        await using var factory = NewFactory(coordinator);
        using var client = factory.CreateClient();

        for (var index = 0; index < 3; index++)
        {
            _ = await CreateJobAsync(factory, client);
        }

        var firstPage = await ReadJobPageAsync(factory, client, "?limit=2&offset=0");
        AssertEx.Equal(expected: 2, firstPage.GetProperty("items").GetArrayLength());
        AssertEx.Equal(expected: 3, firstPage.GetProperty("totalCount").GetInt32(), "TotalCount counts every job, not the page.");

        var secondPage = await ReadJobPageAsync(factory, client, "?limit=2&offset=2");
        AssertEx.Equal(expected: 1, secondPage.GetProperty("items").GetArrayLength());
        AssertEx.Equal(expected: 3, secondPage.GetProperty("totalCount").GetInt32());

        // No bounds named: the handler's default page still carries the total.
        var defaulted = await ReadJobPageAsync(factory, client, query: "");
        AssertEx.Equal(expected: 3, defaulted.GetProperty("items").GetArrayLength());
        AssertEx.Equal(expected: 3, defaulted.GetProperty("totalCount").GetInt32());
    }

    [Test]
    public async Task ListImageJobs_WithOutOfRangeBounds_Returns400()
    {
        await using var factory = NewFactory(new StubImageJobCoordinator());
        using var client = factory.CreateClient();

        foreach (var query in new[]
                 {
                     "?limit=0",
                     "?limit=201",
                     "?offset=-1"
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/images/jobs{query}");
            factory.AddNodeBearerToken(request);
            using var response = await client.SendAsync(request);
            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"images/jobs{query} must be rejected by the validator.");
        }
    }

    [Test]
    public async Task CreateImageJob_WithInvalidSizeStepsOrEditParameters_Returns400WithoutEnqueuing()
    {
        var coordinator = new StubImageJobCoordinator();
        await using var factory = NewFactory(coordinator);
        using var client = factory.CreateClient();
        var source = Guid.NewGuid();

        (object Body, string Message)[] cases =
        [
            (new { modelName = "m", prompt = "p", width = 4096 }, "Width and height must be between 64 and 2048 pixels."),
            (new { modelName = "m", prompt = "p", height = 32 }, "Width and height must be between 64 and 2048 pixels."),
            (new { modelName = "m", prompt = "p", steps = 0 }, "Steps must be at least 1."),
            (new { modelName = "m", prompt = "p", editMode = "inpaint", sourceImageId = source }, "The edit mode must be 'img2img' or 'reference'."),
            (new { modelName = "m", prompt = "p", editMode = "IMG2IMG", sourceImageId = source }, "The edit mode must be 'img2img' or 'reference'."),
            (new { modelName = "m", prompt = "p", editMode = "img2img" }, "An edit mode requires a source image."),
            (new { modelName = "m", prompt = "p", sourceImageId = source }, "A source image requires an edit mode."),
            (new { modelName = "m", prompt = "p", editMode = "reference", sourceImageId = source, strength = 0.5 }, "Strength applies only to img2img edits."),
            (new { modelName = "m", prompt = "p", editMode = "img2img", sourceImageId = source, strength = 1.5 }, "Strength must be between 0 and 1.")
        ];

        foreach (var (body, message) in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/jobs")
            {
                Content = JsonContent.Create(body)
            };
            factory.AddNodeBearerToken(request);
            using var response = await client.SendAsync(request);

            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, message);
            AssertEx.Contains(await response.Content.ReadAsStringAsync(), message);
        }

        AssertEx.Null(coordinator.LastInput, "No refused request may reach the coordinator.");
    }

    [Test]
    public async Task CreateImageJob_WithAnImg2ImgEdit_HandsTheEditToTheCoordinatorAndEchoesIt()
    {
        var coordinator = new StubImageJobCoordinator();
        await using var factory = NewFactory(coordinator);
        using var client = factory.CreateClient();
        var source = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/jobs")
        {
            Content = JsonContent.Create(new
            {
                modelName = "stable-diffusion-1.5",
                prompt = "make it autumn",
                editMode = "img2img",
                sourceImageId = source,
                strength = 0.35
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var input = AssertEx.NotNull(coordinator.LastInput);
        AssertEx.Equal(ImageEditMode.Img2Img, input.EditMode);
        AssertEx.Equal(source, input.SourceImageId);
        AssertEx.Equal(0.35, input.Strength);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal("img2img", created.GetProperty("editMode").GetString());
        AssertEx.Equal(source, created.GetProperty("sourceImageId").GetGuid());
        AssertEx.Equal(0.35, created.GetProperty("strength").GetDouble());
    }

    [Test]
    public async Task CreateImageJob_WhenTheCoordinatorRejectsTheEdit_Returns400WithItsMessage()
    {
        var coordinator = new StubImageJobCoordinator
        {
            Rejection = "The selected model does not support this edit mode."
        };
        await using var factory = NewFactory(coordinator);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/jobs")
        {
            Content = JsonContent.Create(new
            {
                modelName = "stable-diffusion-1.5",
                prompt = "p",
                editMode = "reference",
                sourceImageId = Guid.NewGuid()
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "The selected model does not support this edit mode.");
    }

    [Test]
    public async Task ListImageModels_CarriesThePerInstallEditModesAndNativePixels()
    {
        var registry = Substitute.For<IImageModelRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns([
            new ImageModelRegistryEntry
            {
                ModelName = "sd-1.5",
                RepoId = "leejet/stable-diffusion-1.5-gguf",
                Family = ImageModelFamily.Sd15,
                Kind = ImageModelKind.Txt2Img,
                Parts = [],
                SizeBytes = 1,
                SourceRevision = "main",
                DownloadedAtUtc = DateTimeOffset.UnixEpoch
            },
            QwenEntry("qwen-plain", ImageModelPartRole.Diffusion, ImageModelPartRole.Vae, ImageModelPartRole.Llm),
            QwenEntry("qwen-vision", ImageModelPartRole.Diffusion, ImageModelPartRole.Vae, ImageModelPartRole.Llm, ImageModelPartRole.LlmVision)
        ]);
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageModelRegistry>();
                services.AddSingleton(registry);
            }
        };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/images/models");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("items").EnumerateArray()
                                                                                         .ToDictionary(static m => m.GetProperty("modelName").GetString()!);
        AssertEx.True(EditModesOf(items["sd-1.5"]).SequenceEqual(["img2img"]), "Sd15 offers img2img only.");
        AssertEx.Equal(expected: 512 * 512, items["sd-1.5"].GetProperty("nativePixels").GetInt32());
        AssertEx.True(EditModesOf(items["qwen-plain"]).SequenceEqual(["img2img"]), "A Qwen-Image install without the vision tower offers img2img only.");
        AssertEx.True(EditModesOf(items["qwen-vision"]).SequenceEqual(["img2img", "reference"]), "A Qwen-Image install with LlmVision also offers reference.");

        static string?[] EditModesOf(JsonElement model)
        {
            return [.. model.GetProperty("editModes").EnumerateArray().Select(static mode => mode.GetString())];
        }

        static ImageModelRegistryEntry QwenEntry(string name, params ImageModelPartRole[] roles)
        {
            return new ImageModelRegistryEntry
            {
                ModelName = name,
                RepoId = "unsloth/Qwen-Image-2.1-GGUF",
                Family = ImageModelFamily.QwenImage,
                Kind = ImageModelKind.Txt2Img,
                Parts = [.. roles.Select(static role => new ImageModelPart { Role = role, FileName = $"{role}.gguf", LocalPath = $"/m/{role}.gguf", SizeBytes = 1 })],
                SizeBytes = 1,
                SourceRevision = "main",
                DownloadedAtUtc = DateTimeOffset.UnixEpoch
            };
        }
    }

    private static TestServerWebAppFactory NewFactory(IImageJobCoordinator coordinator)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IImageJobCoordinator>();
                services.AddSingleton(coordinator);
            }
        };
    }

    private static async Task<Guid> CreateJobAsync(TestServerWebAppFactory factory, HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/jobs")
        {
            Content = JsonContent.Create(new
            {
                modelName = "stable-diffusion-1.5",
                prompt = "a watercolor fox"
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return created.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadJobPageAsync(TestServerWebAppFactory factory, HttpClient client, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/images/jobs{query}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }

    // Deterministic in-memory coordinator: EnqueueAsync mints an id and stores a Queued view GetAsync then returns; no
    // sd-server, DbContext, or encryption is exercised. CancelAsync returns false for an unknown id (→ 404).
    private sealed class StubImageJobCoordinator : IImageJobCoordinator
    {
        private readonly ConcurrentDictionary<Guid, ImageJobView> _jobs = new();

        /// <summary>The most recent input handed to <see cref="EnqueueAsync" />; null until the first enqueue.</summary>
        public CreateImageJobInput? LastInput { get; private set; }

        /// <summary>When set, every enqueue is refused with this message, as the coordinator's edit checks do.</summary>
        public string? Rejection { get; init; }

        public Task<Guid> EnqueueAsync(CreateImageJobInput input, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (Rejection is not null)
            {
                throw new ImageJobInputRejectedException(Rejection);
            }

            LastInput = input;
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
                CreatedAtUtc = 0,
                EditMode = input.EditMode,
                SourceImageId = input.SourceImageId,
                Strength = input.Strength
            };
            return Task.FromResult(id);
        }

        public Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult(_jobs.TryRemove(jobId, out _));

        public Task<ImageJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult(_jobs.TryGetValue(jobId, out var view) ? view : null);

        public Task<ImageJobPage> ListAsync(int limit, int offset, CancellationToken cancellationToken)
        {
            var page = _jobs.Values
                            .OrderByDescending(view => view.CreatedAtUtc)
                            .ThenByDescending(view => view.Id)
                            .Skip(offset)
                            .Take(limit)
                            .ToArray();
            return Task.FromResult(new ImageJobPage
            {
                Items = page,
                TotalCount = _jobs.Count
            });
        }

        public Task<ImageJobDeleteOutcome> DeleteAsync(Guid jobId, CancellationToken cancellationToken)
        {
            if (!_jobs.TryGetValue(jobId, out var view))
            {
                return Task.FromResult(ImageJobDeleteOutcome.NotFound);
            }

            if (view.Status is ImageJobStatus.Queued or ImageJobStatus.Generating)
            {
                return Task.FromResult(ImageJobDeleteOutcome.NotTerminal);
            }

            _ = _jobs.TryRemove(jobId, out _);
            return Task.FromResult(ImageJobDeleteOutcome.Deleted);
        }

        /// <summary>Drops the job to a terminal state so a delete against it is not refused.</summary>
        public void Terminalize(Guid jobId)
        {
            if (_jobs.TryGetValue(jobId, out var view))
            {
                _jobs[jobId] = view with
                {
                    Status = ImageJobStatus.Succeeded
                };
            }
        }

        public IReadOnlyList<ImageJobStatusEvent> SnapshotBufferedEvents(Guid jobId) =>
            [];
    }
}
