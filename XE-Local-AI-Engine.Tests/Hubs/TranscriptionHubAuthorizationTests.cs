namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Net;
using System.Net.Http.Headers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Who may open a live transcription connection at all, proven against the wired host rather than by reading the
///     attribute back off the type.
/// </summary>
/// <remarks>
///     The Operator proof is the authenticated NON-operator answering 403. An anonymous 401 proves only that
///     authentication is switched on: swapping the Operator policy for a bare <c>[Authorize]</c> would keep every
///     anonymous case green while opening the hub to any signed-in principal, so 401 alone is never reported as
///     Operator evidence.
/// </remarks>
public sealed class TranscriptionHubAuthorizationTests
{
    /// <summary>
    ///     SignalR's handshake. It does NOT end in <c>/hub</c>, so it authenticates from the Authorization header
    ///     rather than the <c>access_token</c> query the connection path accepts.
    /// </summary>
    private const string NegotiateRoute = LocalApiRoutes.Transcription.Hub + "/negotiate";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Negotiate_WithAnAuthenticatedNonOperator_IsForbidden()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, NegotiateRoute);
        factory.AddNonOperatorBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task Negotiate_WithAnOperator_Succeeds()
    {
        // The control: without it a hub broken for everyone would pass the test above.
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, NegotiateRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateNodeAccessToken());
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public async Task Negotiate_WithNoCredentials_IsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, NegotiateRoute);
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
