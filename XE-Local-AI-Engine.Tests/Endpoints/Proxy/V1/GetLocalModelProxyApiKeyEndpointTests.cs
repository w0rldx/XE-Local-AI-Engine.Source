namespace XE_Local_AI_Engine.Tests.Endpoints.Proxy.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Endpoints.Proxy.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>GET proxy/key</c>: operator-gated, and answers 200 with <c>configured=false</c> (not 404) when no credential
///     exists so the settings page renders the empty state from one call. It also carries the live-request base URL an
///     external tool is configured with.
/// </summary>
public sealed class GetLocalModelProxyApiKeyEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Get_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await LocalModelProxyApiKeyRequests.AnonymousAsync(client, HttpMethod.Get).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await LocalModelProxyApiKeyRequests.AsNonOperatorAsync(Factory, client, HttpMethod.Get).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Get_WhenNoKeyConfigured_Returns200WithConfiguredFalseAndEndpointUrl()
    {
        using var client = Factory.CreateClient();

        using var response = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Get).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<LocalModelProxyApiKeyStatusResponse>().ConfigureAwait(false));
        AssertEx.False(status.Configured, "A node with no minted credential must report configured=false, not 404.");
        AssertEx.Null(status.ApiKey);
        AssertEx.Contains(status.EndpointUrl, "/api/local/v1/proxy/v1", StringComparison.Ordinal);
    }
}
