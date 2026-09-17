namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>Transcription:Enabled</c> gates behaviour, not registration: the routes stay discovered so the OpenAPI
///     document is identical on every node, and a disabled node answers 404 from request-path middleware placed ahead
///     of local API security.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TranscriptionFeatureGateTests
{
    private const string ApiPrefix = "/api/local/v1";

    [Test]
    public async Task TranscriptionRoutes_WhenDisabled_Return404()
    {
        await using var factory = DisabledFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/transcription/runtime");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task TranscriptionRoutes_WhenDisabled_Return404_BeforeAuthenticationCanAnswer403()
    {
        // Asserted with an OPERATOR token, so the 404 is proven to beat authorization rather than coinciding with the
        // 401 an anonymous request would get anyway. That ordering is what stops the switch being probed by status code.
        await using var factory = DisabledFactory();
        using var client = factory.CreateClient();

        foreach (var route in Routes())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            factory.AddNodeBearerToken(request);
            using var response = await client.SendAsync(request).ConfigureAwait(false);

            AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode,
                $"'{route}' must answer 404 while the feature is off, even for an operator.");
        }
    }

    [Test]
    public async Task TranscriptionRoutes_WhenDisabled_Return404ForANonOperatorToo()
    {
        // The disabled surface must not distinguish who is asking: a 403 here would tell an unauthorized caller that
        // the route exists.
        await using var factory = DisabledFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/transcription/runtime");
        factory.AddNonOperatorBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task TranscriptionRoutes_WhenEnabled_DoNotReturn404()
    {
        // The control: without it the test above would pass against a route that simply does not exist.
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/transcription/runtime");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.NotEqual(HttpStatusCode.NotFound, response.StatusCode,
            "With the feature on, the runtime route must be reachable — otherwise the disabled-case 404 proves nothing.");
    }

    private static IEnumerable<string> Routes()
    {
        yield return $"{ApiPrefix}/transcription/runtime";
        yield return $"{ApiPrefix}/transcription/runtime/recommendation";
        yield return $"{ApiPrefix}/transcription/models";
    }

    private static TestServerWebAppFactory DisabledFactory() =>
        new()
        {
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["Transcription:Enabled"] = "false"
            }
        };
}
