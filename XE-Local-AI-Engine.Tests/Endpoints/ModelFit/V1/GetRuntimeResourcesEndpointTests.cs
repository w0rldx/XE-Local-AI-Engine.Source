namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>GET model-fit/resources</c> at the HTTP layer: the exact wire shape (aggregates only, no device names), an
///     empty <c>gpus</c> list passed through as-is, and the operator policy refusing anonymous and non-admin callers
///     before the sampler runs.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GetRuntimeResourcesEndpointTests
{
    private const string ResourcesRoute = "/api/local/v1/model-fit/resources";

    [Test]
    public async Task GetResources_ProjectsTheSampleOntoTheExactWireShape()
    {
        var sampler = SamplerReturning(new LiveMemorySample
        {
            TotalRamBytes = 64_000,
            AvailableRamBytes = 32_000,
            Gpus =
            [
                new GpuMemorySample
                {
                    Index = 0,
                    TotalVramBytes = 24_000,
                    UsedVramBytes = 4_000,
                    AvailableVramBytes = 20_000
                },
                new GpuMemorySample
                {
                    Index = 2,
                    TotalVramBytes = 48_000,
                    UsedVramBytes = 8_000,
                    AvailableVramBytes = 40_000
                }
            ]
        });

        var (status, body) = await GetAsync(sampler, factory => factory.AddNodeBearerToken);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        AssertEx.Equal("availableRamBytes,gpus,totalRamBytes", PropertyNames(root));
        AssertEx.Equal(64_000L, root.GetProperty("totalRamBytes").GetInt64());
        AssertEx.Equal(32_000L, root.GetProperty("availableRamBytes").GetInt64());
        var gpus = root.GetProperty("gpus").EnumerateArray().ToArray();
        AssertEx.Equal(expected: 2, gpus.Length);
        AssertEx.Equal("availableVramBytes,index,totalVramBytes,usedVramBytes", PropertyNames(gpus[0]));
        AssertEx.Equal(expected: 2, gpus[1].GetProperty("index").GetInt32());
        AssertEx.Equal(48_000L, gpus[1].GetProperty("totalVramBytes").GetInt64());
        AssertEx.Equal(8_000L, gpus[1].GetProperty("usedVramBytes").GetInt64());
        AssertEx.Equal(40_000L, gpus[1].GetProperty("availableVramBytes").GetInt64());
    }

    [Test]
    public async Task GetResources_WhenVramIsUnknown_ReturnsAnEmptyGpuList()
    {
        var sampler = SamplerReturning(new LiveMemorySample
        {
            TotalRamBytes = 64_000,
            AvailableRamBytes = 32_000,
            Gpus = []
        });

        var (status, body) = await GetAsync(sampler, factory => factory.AddNodeBearerToken);

        AssertEx.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("gpus").GetArrayLength());
    }

    [Test]
    public async Task GetResources_WithANonAdminToken_IsForbiddenBeforeTheSampler()
    {
        var sampler = Substitute.For<ILiveMemorySampler>();

        var (status, _) = await GetAsync(sampler,
            factory => request => request.Headers.Authorization =
                new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, factory.CreateNonOperatorAccessToken("Viewer")));

        AssertEx.Equal(HttpStatusCode.Forbidden, status);
        await sampler.DidNotReceiveWithAnyArgs().SampleAsync(default);
    }

    [Test]
    public async Task GetResources_WithoutABearerToken_IsUnauthorized()
    {
        var sampler = Substitute.For<ILiveMemorySampler>();

        var (status, _) = await GetAsync(sampler, static _ => static _ => { });

        AssertEx.Equal(HttpStatusCode.Unauthorized, status);
        await sampler.DidNotReceiveWithAnyArgs().SampleAsync(default);
    }

    private static ILiveMemorySampler SamplerReturning(LiveMemorySample sample)
    {
        var sampler = Substitute.For<ILiveMemorySampler>();
        sampler.SampleAsync(Arg.Any<CancellationToken>()).Returns(sample);
        return sampler;
    }

    private static string PropertyNames(JsonElement element)
    {
        return string.Join(',', element.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(ILiveMemorySampler sampler,
        Func<TestServerWebAppFactory, Action<HttpRequestMessage>> authorize)
    {
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILiveMemorySampler>();
                services.AddSingleton(sampler);
            }
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ResourcesRoute);
        authorize(factory)(request);

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
