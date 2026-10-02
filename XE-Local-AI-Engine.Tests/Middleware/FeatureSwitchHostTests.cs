namespace XE_Local_AI_Engine.Tests.Middleware;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The feature-switch middleware on a real host: a saved switch closes its family on the next request, ahead of authentication,
///     while the capability GET stays reachable and reports it.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class FeatureSwitchHostTests
{
    private const string ApiPrefix = "/api/local/v1";

    [Test]
    public async Task SavingWorkSessionsOff_ClosesTheFamilyOnTheNextRequest_AndTheCapabilityReportsIt()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["WorkSessions:Enabled"] = "true"
            }
        };
        using var client = factory.CreateClient();
        AssertEx.NotEqual(HttpStatusCode.NotFound, await GetAsync(factory, client, $"{ApiPrefix}/work-sessions", withToken: true));

        await SaveAsync(factory, client, new SaveNodeSettingsRequest
        {
            WorkSessionsEnabled = false
        });

        AssertEx.Equal(HttpStatusCode.NotFound, await GetAsync(factory, client, $"{ApiPrefix}/work-sessions", withToken: true));
        AssertEx.Equal(HttpStatusCode.NotFound,
            await GetAsync(factory, client, $"{ApiPrefix}/work-sessions", withToken: false),
            "the switch must answer before authentication can answer 401");

        using var capability = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/work-sessions/capability");
        factory.AddNodeBearerToken(capability);
        using var capabilityResponse = await client.SendAsync(capability);
        AssertEx.Equal(HttpStatusCode.OK, capabilityResponse.StatusCode);
        using var body = JsonDocument.Parse(await capabilityResponse.Content.ReadAsStringAsync());
        AssertEx.False(body.RootElement.GetProperty("enabled").GetBoolean());

        await SaveAsync(factory, client, new SaveNodeSettingsRequest
        {
            WorkSessionsEnabled = true
        });
        AssertEx.NotEqual(HttpStatusCode.NotFound, await GetAsync(factory, client, $"{ApiPrefix}/work-sessions", withToken: true));
    }

    private static async Task<HttpStatusCode> GetAsync(TestServerWebAppFactory factory, HttpClient client, string path, bool withToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (withToken)
        {
            factory.AddNodeBearerToken(request);
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task SaveAsync(TestServerWebAppFactory factory, HttpClient client, SaveNodeSettingsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiPrefix}/node-settings");
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
