namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeAuthEndpointTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";

    [Test]
    public async Task AuthFlow_WhenSetupLoginRefreshAndLogout_RunSuccessfully()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        var initialStatus = await client.GetFromJsonAsync<AuthStatusResponse>("/api/local/v1/auth/status").ConfigureAwait(false);
        AssertEx.True(AssertEx.NotNull(initialStatus).SetupRequired);

        using var setupResponse = await SetupAsync(client).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

        using var loginResponse = await LoginAsync(client).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginToken = await ReadTokenAsync(loginResponse).ConfigureAwait(false);
        var loginRefreshCookie = GetRefreshCookie(loginResponse);

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/local/v1/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginToken.AccessToken);
        using var meResponse = await client.SendAsync(meRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", loginRefreshCookie);
        using var refreshResponse = await client.SendAsync(refreshRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshedToken = await ReadTokenAsync(refreshResponse).ConfigureAwait(false);
        var rotatedRefreshCookie = GetRefreshCookie(refreshResponse);

        AssertEx.NotEqual(loginToken.AccessToken, refreshedToken.AccessToken);
        AssertEx.NotEqual(loginRefreshCookie, rotatedRefreshCookie);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/logout");
        logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken.AccessToken);
        logoutRequest.Headers.Add("Cookie", rotatedRefreshCookie);
        using var logoutResponse = await client.SendAsync(logoutRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        AssertRefreshCookieCleared(logoutResponse);
    }

    [Test]
    public async Task Refresh_WhenRotatedTokenIsReplayed_ReturnsUnauthorizedAndClearsCookie()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);
        using var loginResponse = await LoginAsync(client).ConfigureAwait(false);
        var originalRefreshCookie = GetRefreshCookie(loginResponse);

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", originalRefreshCookie);
        using var refreshResponse = await client.SendAsync(refreshRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        replayRequest.Headers.Add("Cookie", originalRefreshCookie);
        using var replayResponse = await client.SendAsync(replayRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        AssertRefreshCookieCleared(replayResponse);
    }

    [Test]
    public async Task Refresh_WhenCookieIsMissing_ReturnsUnauthorizedAndClearsCookie()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/local/v1/auth/refresh", content: null).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertRefreshCookieCleared(response);
    }

    [Test]
    public async Task Setup_WhenAlreadyInitialized_ReturnsConflict()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

        using var duplicateSetupResponse = await SetupAsync(client, "other@example.test").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, duplicateSetupResponse.StatusCode);
    }

    [Test]
    public async Task Setup_WhenConcurrentRequestsRace_CreatesSingleAdmin()
    {
        await using var factory = new TestServerWebAppFactory();
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var responses = await Task.WhenAll(SetupAsync(firstClient, "first@example.test"),
            SetupAsync(secondClient, "second@example.test")).ConfigureAwait(false);

        try
        {
            AssertEx.Equal(expected: 1, responses.Count(response => response.StatusCode == HttpStatusCode.NoContent));
            AssertEx.Equal(expected: 1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task Login_WhenPasswordIsInvalid_ReturnsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password = "wrong-password"
            }).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertRefreshCookieCleared(response);
    }

    // Pins the deliberate shape behind the OpenAPI spec listing only `password` as required on the login request:
    // `NodeLoginRequest.Email` is nullable on purpose, and `NodeAuthService.ResolveLoginUserAsync` resolves the single
    // SetupCompleted user when no email is supplied.
    [Test]
    public async Task Login_WhenEmailIsOmitted_ResolvesTheSingleCompletedAdmin()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password = Password
            }).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await ReadTokenAsync(response).ConfigureAwait(false);
        AssertEx.NotEmpty(token.AccessToken);
    }

    private static Task<HttpResponseMessage> SetupAsync(HttpClient client, string email = Email)
    {
        return client.PostAsJsonAsync("/api/local/v1/auth/setup",
            new
            {
                email,
                password = Password
            });
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        return client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password = Password
            });
    }

    private static async Task<AuthTokenResponse> ReadTokenAsync(HttpResponseMessage response)
    {
        var token = await response.Content.ReadFromJsonAsync<AuthTokenResponse>().ConfigureAwait(false);
        return AssertEx.NotNull(token);
    }

    private static string GetRefreshCookie(HttpResponseMessage response)
    {
        var setCookieHeaders = GetSetCookieHeaders(response);
        var setCookie = AssertEx.NotNull(setCookieHeaders.FirstOrDefault(header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=", StringComparison.Ordinal)));
        var cookieValue = setCookie.Split(separator: ';', count: 2)[0];
        AssertEx.NotEmpty(cookieValue);
        AssertEx.Contains(setCookie, "httponly", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(setCookie, "secure", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(setCookie, "samesite=strict", StringComparison.OrdinalIgnoreCase);
        return cookieValue;
    }

    private static void AssertRefreshCookieCleared(HttpResponseMessage response)
    {
        var setCookieHeaders = GetSetCookieHeaders(response);
        AssertEx.Contains(setCookieHeaders, header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=;", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> GetSetCookieHeaders(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
    }

    private sealed record AuthStatusResponse(bool SetupRequired, bool Authenticated);

    private sealed record AuthTokenResponse(string AccessToken, DateTime ExpiresAtUtc);
}
