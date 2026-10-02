namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The transcription switch gates behaviour, not registration: the routes stay discovered, and a disabled node answers 404
///     from request-path middleware placed ahead of local API security.
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
        using var response = await client.SendAsync(request);

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
            using var response = await client.SendAsync(request);

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
        using var response = await client.SendAsync(request);

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
        using var response = await client.SendAsync(request);

        AssertEx.NotEqual(HttpStatusCode.NotFound, response.StatusCode,
            "With the feature on, the runtime route must be reachable — otherwise the disabled-case 404 proves nothing.");
    }

    [Test]
    public async Task TranscriptionRoutes_WhenTheStoredSettingIsOff_Return404_OverASeedOfOn()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["Transcription:Enabled"] = "true"
            }
        };
        _ = Directory.CreateDirectory(factory.NodeDataDirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(factory.NodeDataDirectoryPath, "node-settings.json"), """{ "transcriptionEnabled": false }""");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/transcription/runtime");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task ASavedSwitch_ClosesAndReopensTheRoutes_WithoutARestart()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        AssertEx.NotEqual(HttpStatusCode.NotFound, await GetRuntimeStatusAsync(factory, client));
        await SaveTranscriptionEnabledAsync(factory, client, enabled: false);
        AssertEx.Equal(HttpStatusCode.NotFound, await GetRuntimeStatusAsync(factory, client), "the next request after the save must see the switch off");
        await SaveTranscriptionEnabledAsync(factory, client, enabled: true);
        AssertEx.NotEqual(HttpStatusCode.NotFound, await GetRuntimeStatusAsync(factory, client));
    }

    private static async Task<HttpStatusCode> GetRuntimeStatusAsync(TestServerWebAppFactory factory, HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/transcription/runtime");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task SaveTranscriptionEnabledAsync(TestServerWebAppFactory factory, HttpClient client, bool enabled)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiPrefix}/node-settings");
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        request.Content = JsonContent.Create(new SaveNodeSettingsRequest
        {
            TranscriptionEnabled = enabled
        });
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
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
