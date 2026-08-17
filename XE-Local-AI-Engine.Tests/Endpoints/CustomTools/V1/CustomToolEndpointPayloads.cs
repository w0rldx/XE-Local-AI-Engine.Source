namespace XE_Local_AI_Engine.Tests.Endpoints.CustomTools.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Wire payloads and helpers shared by the six custom-tool endpoint suites. The request bodies are anonymous objects
///     rather than the <c>CustomToolDefinition</c> record on purpose: these tests assert the HTTP contract the React
///     client and the generated SDK actually send, so a rename on the DTO surfaces as a failing request here.
/// </summary>
internal static class CustomToolEndpointPayloads
{
    public const string DefinitionsRoute = "/api/local/v1/custom-tools";

    /// <summary>The masking sentinel the CRUD read path substitutes for any stored secret value.</summary>
    public const string SecretSentinel = CustomToolSecrets.Sentinel;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    /// <summary>A minimal, valid HttpFetch tool. <paramref name="secretHeaderValue" /> adds a header marked secret.</summary>
    public static object HttpFetchDefinition(string name,
        bool acknowledged = true,
        string? secretHeaderValue = null,
        string urlTemplate = "https://api.example.com/things",
        bool enabled = true)
    {
        object[] headers = secretHeaderValue is null
            ? []
            :
            [
                new
                {
                    name = "X-Api-Key",
                    value = secretHeaderValue,
                    isSecret = true
                }
            ];

        return new
        {
            name,
            description = "Endpoint suite fixture tool.",
            kind = "HttpFetch",
            mode = "Fixed",
            enabled,
            acknowledged,
            parameters = Array.Empty<object>(),
            http = new
            {
                method = "GET",
                urlTemplate,
                headers,
                allowedHosts = Array.Empty<string>()
            }
        };
    }

    /// <summary>Creates a tool through the real POST endpoint and returns its id, failing loudly if the create did not 201.</summary>
    public static async Task<Guid> CreateAsync(TestServerWebAppFactory factory, HttpClient client, string name, string? secretHeaderValue = null)
    {
        using var response = await SendAsOperatorAsync(factory,
            client,
            HttpMethod.Post,
            DefinitionsRoute,
            HttpFetchDefinition(name, acknowledged: true, secretHeaderValue)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode, $"Seeding the custom tool '{name}' must succeed.");

        var view = await response.Content.ReadFromJsonAsync<CustomToolView>(Json).ConfigureAwait(false);
        return AssertEx.NotNull(view).Id;
    }

    /// <summary>Sends <paramref name="method" /> to <paramref name="route" /> as the operator (Admin role), with an optional JSON body.</summary>
    public static Task<HttpResponseMessage> SendAsOperatorAsync(TestServerWebAppFactory factory,
        HttpClient client,
        HttpMethod method,
        string route,
        object? body = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return SendAsync(client, method, route, body, factory.AddNodeBearerToken);
    }

    /// <summary>Sends the same request as an authenticated principal that is NOT the operator (no Admin role).</summary>
    public static Task<HttpResponseMessage> SendAsNonOperatorAsync(TestServerWebAppFactory factory,
        HttpClient client,
        HttpMethod method,
        string route,
        object? body = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return SendAsync(client, method, route, body, factory.AddNonOperatorBearerToken);
    }

    /// <summary>Sends the same request with no credentials at all.</summary>
    public static Task<HttpResponseMessage> SendAnonymousAsync(HttpClient client,
        HttpMethod method,
        string route,
        object? body = null)
    {
        return SendAsync(client, method, route, body, authenticate: null);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        HttpMethod method,
        string route,
        object? body,
        Action<HttpRequestMessage>? authenticate)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        authenticate?.Invoke(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
