namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ResponseSecurityHeaderTests
{
    private const string AntiFramingHeaderName = "X-Frame-Options";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task StaticResponse_CarriesDenyAntiFramingHeader()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync("/index.html").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertDenyAntiFramingHeader(response);
    }

    [Test]
    public async Task HealthResponse_CarriesDenyAntiFramingHeader()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync("/health/live").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertDenyAntiFramingHeader(response);
    }

    [Test]
    public async Task UnauthorizedApiResponse_CarriesDenyAntiFramingHeader()
    {
        using var client = Factory.CreateClient();

        using var response = await client.PostAsync("/api/local/v1/diagnostics/validation-probe", content: null).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertDenyAntiFramingHeader(response);
    }

    private static void AssertDenyAntiFramingHeader(HttpResponseMessage response)
    {
        AssertEx.True(response.Headers.TryGetValues(AntiFramingHeaderName, out var values));
        AssertEx.Equal("DENY", AssertEx.NotNull(values).Single());
    }
}
