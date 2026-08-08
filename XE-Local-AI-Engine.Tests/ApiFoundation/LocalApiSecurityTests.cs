namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalApiSecurityTests
{
    [Test]
    public async Task LocalApi_WhenTokenIsMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/local/v1/diagnostics/validation-probe",
            new
            {
                Name = "operator"
            }).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task LocalApi_WhenTokenIsInvalid_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        using var request = CreateProbeRequest();
        request.Headers.Add("Authorization", "Bearer invalid-token");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task LocalApi_WhenHostIsUnsafe_ReturnsBadRequest()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        using var request = CreateProbeRequest(factory);
        request.Headers.Host = "evil.example";

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task LocalApi_WhenOriginIsUnsafe_ReturnsForbidden()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        using var request = CreateProbeRequest(factory);
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task LocalApi_WhenTokenAndSameOriginAreValid_AllowsRequest()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        using var request = CreateProbeRequest(factory);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage CreateProbeRequest(TestingWebAppFactory factory)
    {
        var request = CreateProbeRequest();
        factory.AddNodeBearerToken(request);
        return request;
    }

    private static HttpRequestMessage CreateProbeRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/diagnostics/validation-probe")
        {
            Content = JsonContent.Create(new
            {
                Name = "operator"
            })
        };
        return request;
    }
}
