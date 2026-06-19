namespace XE_Local_AI_Engine.Tests.Endpoints.Cloud.Codex;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Cloud.Codex.V1;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
/// Covers the Operator Codex endpoints: login returns the authorize URL, status reflects the
/// session + pending state without token material, logout clears the session, and all routes reject callers
/// without an operator token.
/// </summary>
public sealed class CodexEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Login_ReturnsAuthorizeUrl()
    {
        var coordinator = Substitute.For<ICodexLoginCoordinator>();
        coordinator.Start().Returns(new Uri("https://auth.openai.com/authorize?code_challenge=abc"));
        await using var factory = CreateFactory(loginCoordinator: coordinator);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/cloud/codex/login");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var login = Deserialize<CodexLoginResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("https://auth.openai.com/authorize?code_challenge=abc", login.AuthorizeUrl);
        coordinator.Received(1).Start();
    }

    [Test]
    public async Task Status_WhenSignedIn_ReportsAccountAndExpiry_WithoutTokenMaterial()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var tokenStore = Substitute.For<ICodexTokenStore>();
        tokenStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new CodexTokens("secret-access", "secret-refresh", expiry, "acct_42"));
        var coordinator = Substitute.For<ICodexLoginCoordinator>();
        coordinator.GetStatus().Returns(CodexLoginStatus.None);
        await using var factory = CreateFactory(tokenStore, coordinator);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/cloud/codex/status");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var status = Deserialize<CodexStatusResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(status.SignedIn);
        AssertEx.Equal("acct_42", status.AccountId);
        AssertEx.False(status.LoginPending);
        AssertEx.False(body.Contains("secret-access", StringComparison.Ordinal), "access token must never be returned");
        AssertEx.False(body.Contains("secret-refresh", StringComparison.Ordinal), "refresh token must never be returned");
    }

    [Test]
    public async Task Status_WhenSessionExpired_ReportsNotSignedIn_ButKeepsAccountAndExpiry()
    {
        // A stale session must NOT report SignedIn=true with a past ExpiresAtUtc. Account id +
        // expiry stay populated so the UI can show a "session expired — re-authenticate" state.
        var pastExpiry = DateTimeOffset.UtcNow.AddHours(-1);
        var tokenStore = Substitute.For<ICodexTokenStore>();
        tokenStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new CodexTokens("a", "r", pastExpiry, "acct_expired"));
        var coordinator = Substitute.For<ICodexLoginCoordinator>();
        coordinator.GetStatus().Returns(CodexLoginStatus.None);
        await using var factory = CreateFactory(tokenStore, coordinator);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/cloud/codex/status");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var status = Deserialize<CodexStatusResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(status.SignedIn, "an expired access token must report SignedIn=false");
        AssertEx.Equal("acct_expired", status.AccountId);
        AssertEx.True(status.ExpiresAtUtc.HasValue, "expiry must remain populated for the re-auth prompt");
        AssertEx.Equal(pastExpiry, status.ExpiresAtUtc!.Value);
    }

    [Test]
    public async Task Status_WhenLoginPending_ReportsPendingNotSignedIn()
    {
        var tokenStore = Substitute.For<ICodexTokenStore>();
        tokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
        var coordinator = Substitute.For<ICodexLoginCoordinator>();
        coordinator.GetStatus()
            .Returns(new CodexLoginStatus(CodexLoginState.Pending, new Uri("https://auth.openai.com/authorize")));
        await using var factory = CreateFactory(tokenStore, coordinator);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/cloud/codex/status");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var status = Deserialize<CodexStatusResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(status.SignedIn);
        AssertEx.True(status.LoginPending);
    }

    [Test]
    public async Task Logout_ClearsSession_AndReportsSignedOut()
    {
        var tokenStore = Substitute.For<ICodexTokenStore>();
        await using var factory = CreateFactory(tokenStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/cloud/codex/logout");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var status = Deserialize<CodexStatusResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(status.SignedIn);
        await tokenStore.Received(1).ClearAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CodexEndpoints_WhenTokenMissing_AreRejected()
    {
        var coordinator = Substitute.For<ICodexLoginCoordinator>();
        var tokenStore = Substitute.For<ICodexTokenStore>();
        await using var factory = CreateFactory(tokenStore, coordinator);
        using var client = factory.CreateClient();

        // No operator bearer token attached → all codex routes must reject.
        using var loginResponse = await client.PostAsync("/api/local/v1/cloud/codex/login", content: null).ConfigureAwait(false);
        using var statusResponse = await client.GetAsync("/api/local/v1/cloud/codex/status").ConfigureAwait(false);
        using var logoutResponse = await client.PostAsync("/api/local/v1/cloud/codex/logout", content: null).ConfigureAwait(false);

        AssertEx.True(IsRejected(loginResponse.StatusCode), $"login was {loginResponse.StatusCode}");
        AssertEx.True(IsRejected(statusResponse.StatusCode), $"status was {statusResponse.StatusCode}");
        AssertEx.True(IsRejected(logoutResponse.StatusCode), $"logout was {logoutResponse.StatusCode}");

        coordinator.DidNotReceive().Start();
        await tokenStore.DidNotReceive().ClearAsync(Arg.Any<CancellationToken>());
    }

    private static bool IsRejected(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static TestingWebAppFactory CreateFactory(
        ICodexTokenStore? tokenStore = null,
        ICodexLoginCoordinator? loginCoordinator = null)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ICodexTokenStore>();
                services.AddSingleton(tokenStore ?? Substitute.For<ICodexTokenStore>());
                services.RemoveAll<ICodexLoginCoordinator>();
                services.AddSingleton(loginCoordinator ?? Substitute.For<ICodexLoginCoordinator>());
                // Substitute the active-cloud factory so the logout endpoint's dependency does not drag the real
                // Codex chat-client / auth-handler chain (which needs an HttpClient the test factory stubs to null)
                // into endpoint instantiation at host startup.
                services.RemoveAll<IActiveCloudChatClientFactory>();
                services.AddSingleton(Substitute.For<IActiveCloudChatClientFactory>());
            },
        };
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static T Deserialize<T>(string body)
        where T : class
        => AssertEx.NotNull(JsonSerializer.Deserialize<T>(body, JsonOptions));
}
