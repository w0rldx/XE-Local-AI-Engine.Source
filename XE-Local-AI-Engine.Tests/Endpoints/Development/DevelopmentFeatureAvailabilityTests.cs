namespace XE_Local_AI_Engine.Tests.Endpoints.Development;

using System.Net;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel("DevelopmentFeatureConfiguration")]
public sealed class DevelopmentFeatureAvailabilityTests
{
    [Test]
    public async Task ListProjects_WhenConfigurationIsAbsentAndTokenIsMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/local/v1/development/projects").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ListProjects_WhenConfigurationIsAbsent_IsEnabledByDefault()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local/v1/development/projects");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task Negotiate_WhenDevelopmentModeIsDisabled_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory
        {
            EnableDevelopmentMode = false
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, LocalApiRoutes.Development.Hub + "/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://localhost");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Capability_ReturnsEffectiveRuntimeState(bool enabled)
    {
        await using var factory = new TestingWebAppFactory
        {
            EnableDevelopmentMode = enabled
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local/v1/development/capability");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = enabled ? "\"enabled\":true" : "\"enabled\":false";
        AssertEx.True(json.Contains(expected, StringComparison.Ordinal));
    }

    [Test]
    public async Task ListProjects_WhenExplicitlyDisabled_ReturnsNotFound()
    {
        await using var factory = new TestingWebAppFactory
        {
            EnableDevelopmentMode = false
        };
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local/v1/development/projects");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
