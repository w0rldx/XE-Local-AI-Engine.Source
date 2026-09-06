namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint tests for the Inference Optimizer operator surface (model-fit/profiles). Covers: the Operator gate
///     (no bearer token → 401); explore role validation (an unknown role 400s before the service is touched); the empty
///     profile-id guard on a body-carried id (400); a service-level domain failure (freeze without a justifying
///     benchmark) mapping to a 400 with an error body; and the list projection surfacing profiles while NEVER leaking the
///     local-only machine key. The <see cref="IInferenceProfileService" /> is substituted so the assertions are
///     deterministic and never spawn a real llama-server.
/// </summary>
public sealed class InferenceProfileEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static string ProfilesRoute()
    {
        return $"{ApiPrefix}/model-fit/profiles";
    }

    private static string ExploreRoute()
    {
        return $"{ApiPrefix}/model-fit/profiles/explore";
    }

    private static string BenchmarkRoute()
    {
        return $"{ApiPrefix}/model-fit/profiles/benchmark";
    }

    private static string FreezeRoute()
    {
        return $"{ApiPrefix}/model-fit/profiles/freeze";
    }

    [Test]
    public async Task ExploreProfile_WhenNoBearerToken_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory(Substitute.For<IInferenceProfileService>());
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ExploreRoute())
        {
            Content = JsonContent.Create(new
            {
                modelName = "some/model-GGUF",
                role = "chat"
            })
        };
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ExploreProfile_WhenRoleUnknown_ReturnsBadRequest()
    {
        var service = Substitute.For<IInferenceProfileService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ExploreRoute())
        {
            Content = JsonContent.Create(new
            {
                modelName = "some/model-GGUF",
                role = "not-a-role"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The role is rejected at the transport boundary — the service must never be reached. Targeted at the
        // four-argument overload the endpoint actually calls: asserting against the three-argument one would pass
        // vacuously and stop verifying anything.
        await service.DidNotReceiveWithAnyArgs()
                     .ExploreAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BenchmarkProfile_WhenProfileIdEmpty_ReturnsBadRequest()
    {
        var service = Substitute.For<IInferenceProfileService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, BenchmarkRoute())
        {
            Content = JsonContent.Create(new
            {
                profileId = Guid.Empty
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await service.DidNotReceiveWithAnyArgs()
                     .BenchmarkAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExploreProfile_WhenSkippedBecauseTheModelIsInUse_ReturnsTheRetryWording()
    {
        const string skipText =
            "Skipped: some/model-GGUF (Chat) is serving 1 in-flight request(s); profiling did not run and nothing was evicted. Retry when the model is idle.";
        var service = Substitute.For<IInferenceProfileService>();
        service.ExploreAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
               .Returns(ExploreResult.SkippedInUse(skipText));

        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ExploreRoute())
        {
            Content = JsonContent.Create(new
            {
                modelName = "some/model-GGUF",
                role = "chat"
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // Still a 400 (the response DTO carries no skip state — a 200-with-skip would change the OpenAPI contract),
        // but the operator reads a retry instruction rather than a failure.
        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Contains(payload, skipText, StringComparison.Ordinal);
    }

    [Test]
    public async Task FreezeProfile_WhenDomainFailure_ReturnsBadRequest()
    {
        var profileId = Guid.NewGuid();
        var service = Substitute.For<IInferenceProfileService>();
        service.FreezeAsync(profileId, Arg.Any<CancellationToken>())
               .Returns(ProfileActionResult.Fail("No successful benchmark justifies a freeze."));

        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, FreezeRoute())
        {
            Content = JsonContent.Create(new
            {
                profileId
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.True(payload.Length > 0, "A domain-failure freeze must return a non-empty error body.");
    }

    [Test]
    public async Task ListProfiles_WhenAuthorized_ReturnsProfiles_OmitsMachineKey()
    {
        var profileId = Guid.NewGuid();
        var service = Substitute.For<IInferenceProfileService>();
        service.ListProfilesAsync(Arg.Any<CancellationToken>())
               .Returns([
                   new InferenceProfileView(profileId,
                       ModelName: "unsloth/gemma-3-12b-it-GGUF",
                       Role: 0,
                       Backend: "vulkan",
                       LlamacppBuild: "b9692",
                       Quant: "Q4_K_M",
                       CtxSize: 8192,
                       NGpuLayers: 33,
                       TensorSplit: null,
                       OverrideTensor: null,
                       KvTypeK: "q8_0",
                       KvTypeV: "q8_0",
                       FlashAttn: true,
                       NParams: 12_000_000_000,
                       IsMoe: false,
                       ExpertCount: null,
                       Status: "Frozen",
                       BenchmarkSnapshotId: Guid.NewGuid(),
                       CreatedAtUtc: 1,
                       UpdatedAtUtc: 2,
                       GlobalFreeVramAtFreezeBytes: 6_000_000_000)
               ]);

        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ProfilesRoute());
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        AssertEx.True(doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array,
            "Profiles response must wrap results in an 'items' array.");
        AssertEx.Equal(expected: 1, items.GetArrayLength());

        var first = items[0];
        AssertEx.Equal(profileId.ToString(), first.GetProperty("id").GetString());
        AssertEx.Equal("chat", first.GetProperty("role").GetString());
        AssertEx.Equal("Frozen", first.GetProperty("status").GetString());

        // The local-only machine key must NEVER leave the box.
        AssertEx.False(json.Contains("machineKey", StringComparison.OrdinalIgnoreCase), "Profiles response must not expose the machine key.");
        AssertEx.False(json.Contains("machine_key", StringComparison.OrdinalIgnoreCase), "Profiles response must not expose the machine key.");
    }

    [Test]
    [Arguments(511)]
    [Arguments(2047)]
    public async Task Explore_WhenContextTokensBelowMinimum_Returns400(int contextTokens)
    {
        var service = Substitute.For<IInferenceProfileService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ExploreRoute())
        {
            Content = JsonContent.Create(new
            {
                modelName = "some/model-GGUF",
                role = "chat",
                contextTokens
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await service.DidNotReceiveWithAnyArgs()
                     .ExploreAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Explore_WhenContextTokensAboveMaximum_Returns400()
    {
        var service = Substitute.For<IInferenceProfileService>();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ExploreRoute())
        {
            Content = JsonContent.Create(new
            {
                modelName = "some/model-GGUF",
                role = "chat",
                contextTokens = 2_000_000
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await service.DidNotReceiveWithAnyArgs()
                     .ExploreAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Explore_WhenContextTokensSupplied_FlowsToServiceOverload()
    {
        var profileId = Guid.NewGuid();
        var service = Substitute.For<IInferenceProfileService>();
        service.ExploreAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
               .Returns(ExploreResult.Ok(ExploredProfile(profileId, ctxSize: 32768)));

        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ExploreRoute())
        {
            Content = JsonContent.Create(new
            {
                modelName = "some/model-GGUF",
                role = "chat",
                contextTokens = 32768
            })
        };
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var profile = doc.RootElement.GetProperty("profile");
        AssertEx.Equal(profileId.ToString(), profile.GetProperty("id").GetString());

        // The effective window is read off the projected profile — the endpoint does not echo the request value.
        AssertEx.Equal(expected: 32768, profile.GetProperty("ctxSize").GetInt32());
        await service.Received(1).ExploreAsync("some/model-GGUF", ModelRole.Chat, 32768, Arg.Any<CancellationToken>());
    }

    private static InferenceProfileView ExploredProfile(Guid profileId, int ctxSize)
    {
        return new InferenceProfileView(profileId,
            ModelName: "some/model-GGUF",
            Role: 0,
            Backend: "cuda",
            LlamacppBuild: "b10201",
            Quant: "Q4_K_M",
            CtxSize: ctxSize,
            NGpuLayers: 33,
            TensorSplit: null,
            OverrideTensor: null,
            KvTypeK: null,
            KvTypeV: null,
            FlashAttn: false,
            NParams: 27_000_000_000,
            IsMoe: false,
            ExpertCount: null,
            Status: "Explored",
            BenchmarkSnapshotId: null,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 2);
    }

    private static TestServerWebAppFactory CreateFactory(IInferenceProfileService inferenceProfileService)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IInferenceProfileService>();
                services.AddSingleton(inferenceProfileService);
            }
        };
    }
}
