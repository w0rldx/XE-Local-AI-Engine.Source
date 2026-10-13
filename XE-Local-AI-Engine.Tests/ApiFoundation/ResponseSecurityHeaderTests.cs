namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class ResponseSecurityHeaderTests
{
    private const string AntiFramingHeaderName = "X-Frame-Options";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task StaticResponse_CarriesDenyAntiFramingHeader()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync("/index.html");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertDenyAntiFramingHeader(response);
    }

    [Test]
    public async Task HealthResponse_CarriesDenyAntiFramingHeader()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertDenyAntiFramingHeader(response);
    }

    [Test]
    public async Task UnauthorizedApiResponse_CarriesDenyAntiFramingHeader()
    {
        using var client = Factory.CreateClient();

        using var response = await client.PostAsync("/api/local/v1/diagnostics/validation-probe", content: null);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertDenyAntiFramingHeader(response);
    }

    // Every response the anti-framing header reaches also carries the no-sniff and referrer headers.
    private static void AssertDenyAntiFramingHeader(HttpResponseMessage response)
    {
        AssertEx.True(response.Headers.TryGetValues(AntiFramingHeaderName, out var values));
        AssertEx.Equal("DENY", AssertEx.NotNull(values).Single());
        AssertEx.True(response.Headers.TryGetValues("X-Content-Type-Options", out var sniff), "nosniff must be sent.");
        AssertEx.Equal("nosniff", AssertEx.NotNull(sniff).Single());
        AssertEx.True(response.Headers.TryGetValues("Referrer-Policy", out var referrer), "Referrer-Policy must be sent.");
        AssertEx.Equal("same-origin", AssertEx.NotNull(referrer).Single());
    }
}
