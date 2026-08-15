namespace XE_Local_AI_Engine.Tests.Endpoints.Proxy.V1;

using System.Net;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Endpoints.Proxy.V1;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>DELETE proxy/key</c>: operator-gated, 404 when there is nothing to revoke, and 204 followed by
///     <c>configured=false</c> when there is — the documented way to turn the inbound proxy off without restarting.
///     <para>Serialized for the same single-credential reason as the generate suite.</para>
/// </summary>
[NotInParallel("LocalModelProxyApiKeyRevoke")]
public sealed class RevokeLocalModelProxyApiKeyEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Revoke_WhenAnonymous_Returns401()
    {
        using var client = Factory.CreateClient();

        using var response = await LocalModelProxyApiKeyRequests.AnonymousAsync(client, HttpMethod.Delete).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task Revoke_WhenAuthenticatedButNotOperator_Returns403()
    {
        using var client = Factory.CreateClient();

        using var response = await LocalModelProxyApiKeyRequests.AsNonOperatorAsync(Factory, client, HttpMethod.Delete).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Revoke_WhenKeyExists_Returns204ThenStatusReportsUnconfigured()
    {
        using var client = Factory.CreateClient();

        using var minted = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Post).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, minted.StatusCode);

        using var revoked = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Delete).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        using var status = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Get).ConfigureAwait(false);
        var statusView = AssertEx.NotNull(await status.Content.ReadFromJsonAsync<LocalModelProxyApiKeyStatusResponse>().ConfigureAwait(false));
        AssertEx.False(statusView.Configured, "A revoked node authenticates nobody on the proxy surface.");
        AssertEx.Null(statusView.ApiKey);

        // The second revoke has nothing to remove, so it must say so rather than report a phantom success.
        using var again = await LocalModelProxyApiKeyRequests.AsOperatorAsync(Factory, client, HttpMethod.Delete).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }
}
