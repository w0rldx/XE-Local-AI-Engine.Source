namespace XE_Local_AI_Engine.Tests.Endpoints.Proxy.V1;

/// <summary>Request helpers shared by the three inbound model-proxy credential suites.</summary>
internal static class LocalModelProxyApiKeyRequests
{
    public const string Route = "/api/local/v1/proxy/key";

    public static Task<HttpResponseMessage> AsOperatorAsync(TestServerWebAppFactory factory, HttpClient client, HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return SendAsync(client, method, factory.AddNodeBearerToken);
    }

    public static Task<HttpResponseMessage> AsNonOperatorAsync(TestServerWebAppFactory factory, HttpClient client, HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return SendAsync(client, method, factory.AddNonOperatorBearerToken);
    }

    public static Task<HttpResponseMessage> AnonymousAsync(HttpClient client, HttpMethod method)
    {
        return SendAsync(client, method, authenticate: null);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Action<HttpRequestMessage>? authenticate)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(method, Route);
        authenticate?.Invoke(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
