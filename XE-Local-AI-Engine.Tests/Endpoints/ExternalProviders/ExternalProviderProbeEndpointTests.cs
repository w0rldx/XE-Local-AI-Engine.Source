namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalProviders;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>How each probe outcome reaches the wire, and what the probe route refuses to carry.</summary>
public sealed class ExternalProviderProbeEndpointTests
{
    private const string ProbeRoute = "/api/local/v1/external-providers/probe";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Probe_WhenTheEndpointAnswers_Returns200WithTheModelIds()
    {
        var probeService = Substitute.For<IExternalProviderProbeService>();
        probeService.ProbeAsync(Arg.Any<ExternalProviderProbeQuery>(), Arg.Any<CancellationToken>())
                    .Returns(new ExternalProviderProbeResult
                    {
                        Outcome = ExternalProviderProbeOutcome.Answered,
                        Models = [new ExternalProviderProbeModel("qwen3-27b", ContextLength: 32768)]
                    });
        await using var factory = CreateFactory(probeService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory);
        request.Content = JsonContent.Create(new ExternalProviderProbeRequest
        {
            ConnectionId = "unsloth-box",
            BaseUrl = "http://127.0.0.1:18099",
            ApiKey = "sk-typed-in-the-form"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var probe = Deserialize<ExternalProviderProbeResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(probe.Reachable);
        AssertEx.Equal("qwen3-27b", probe.Models.Single().Id);
        AssertEx.Equal(expected: 32768, probe.Models.Single().ContextLength);

        // A key the operator just typed is USED and never echoed — the response is rendered in a settings page whose
        // screenshots and bug reports travel.
        AssertEx.False(body.Contains("sk-typed-in-the-form", StringComparison.Ordinal));
        await probeService.Received(1).ProbeAsync(
            Arg.Is<ExternalProviderProbeQuery>(query =>
                query.ConnectionId == "unsloth-box" && query.BaseUrl == "http://127.0.0.1:18099" && query.ApiKey == "sk-typed-in-the-form"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Probe_WhenTheEndpointServesNoListing_IsA200RatherThanAFailure()
    {
        var probeService = Substitute.For<IExternalProviderProbeService>();
        probeService.ProbeAsync(Arg.Any<ExternalProviderProbeQuery>(), Arg.Any<CancellationToken>())
                    .Returns(new ExternalProviderProbeResult
                    {
                        Outcome = ExternalProviderProbeOutcome.Answered,
                        Error = "The endpoint answered with HTTP 404 and served no model listing. Add model ids by hand."
                    });
        await using var factory = CreateFactory(probeService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory);
        request.Content = JsonContent.Create(new ExternalProviderProbeRequest
        {
            BaseUrl = "http://127.0.0.1:18099"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var probe = await ReadJsonAsync<ExternalProviderProbeResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(probe.Reachable);
        AssertEx.Empty(probe.Models);
        AssertEx.NotNull(probe.Error);
    }

    [Test]
    public async Task Probe_WhenTheEndpointIsUnreachable_Returns200WithReachableFalse()
    {
        var probeService = Substitute.For<IExternalProviderProbeService>();
        probeService.ProbeAsync(Arg.Any<ExternalProviderProbeQuery>(), Arg.Any<CancellationToken>())
                    .Returns(new ExternalProviderProbeResult
                    {
                        Outcome = ExternalProviderProbeOutcome.Unreachable,
                        Error = "The endpoint could not be reached."
                    });
        await using var factory = CreateFactory(probeService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory);
        request.Content = JsonContent.Create(new ExternalProviderProbeRequest
        {
            BaseUrl = "http://127.0.0.1:18099"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var probe = await ReadJsonAsync<ExternalProviderProbeResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(probe.Reachable);
    }

    [Test]
    public async Task Probe_WhenTheConnectionIsUnknown_Returns404()
    {
        var probeService = Substitute.For<IExternalProviderProbeService>();
        probeService.ProbeAsync(Arg.Any<ExternalProviderProbeQuery>(), Arg.Any<CancellationToken>())
                    .Returns(new ExternalProviderProbeResult
                    {
                        Outcome = ExternalProviderProbeOutcome.UnknownConnection
                    });
        await using var factory = CreateFactory(probeService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory);
        request.Content = JsonContent.Create(new ExternalProviderProbeRequest
        {
            ConnectionId = "not-configured"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Probe_WhenTheBaseUrlIsUnusable_Returns400()
    {
        var probeService = Substitute.For<IExternalProviderProbeService>();
        probeService.ProbeAsync(Arg.Any<ExternalProviderProbeQuery>(), Arg.Any<CancellationToken>())
                    .Returns(new ExternalProviderProbeResult
                    {
                        Outcome = ExternalProviderProbeOutcome.InvalidBaseUrl,
                        Error = "The endpoint must be an absolute http(s) address."
                    });
        await using var factory = CreateFactory(probeService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory);
        request.Content = JsonContent.Create(new ExternalProviderProbeRequest
        {
            BaseUrl = "ftp://box/v1"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task Probe_WhenNothingIsNamedToProbe_Returns400WithoutCallingTheService()
    {
        var probeService = Substitute.For<IExternalProviderProbeService>();
        await using var factory = CreateFactory(probeService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory);
        request.Content = JsonContent.Create(new ExternalProviderProbeRequest());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await probeService.DidNotReceiveWithAnyArgs().ProbeAsync(Arg.Any<ExternalProviderProbeQuery>(), Arg.Any<CancellationToken>());
    }

    private static TestServerWebAppFactory CreateFactory(IExternalProviderProbeService probeService)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IExternalProviderProbeService>();
                services.AddSingleton(probeService);
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ProbeRoute);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(json, JsonOptions));
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}
