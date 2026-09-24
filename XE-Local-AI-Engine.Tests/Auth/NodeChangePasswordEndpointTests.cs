namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for <c>POST auth/change-password</c> (<see cref="NodeChangePasswordEndpoint" />), the operator's own
///     password change. The contract the SPA depends on is that a SUCCESS ends every session: Identity rotates the
///     security stamp (so the access token already in the caller's hands stops validating), the service revokes every
///     refresh token and the endpoint clears the caller's refresh cookie, and 204 carries no replacement pair. A
///     REJECTION must change nothing at all.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeChangePasswordEndpointTests
{
    private const string Email = "admin@example.test";
    private const string OldPassword = "Str0ng!Password123";
    private const string NewPassword = "R3placed!Password456";

    // Any Operator-policy endpoint proves a bearer token still authenticates; GET node-settings is the cheapest stable
    // one (it reads the settings file and takes no parameters).
    private const string OperatorProbeUrl = "/api/local/v1/node-settings";

    [Test]
    public async Task ChangePassword_WhenCurrentPasswordIsWrong_IsRejectedAndChangesNothing()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);
        var session = await SignInAsync(client, OldPassword);

        using var response = await ChangeAsync(client, session.AccessToken, "Wr0ng!Password999", NewPassword);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<NodeAuthErrorResponse>());
        AssertEx.NotEmpty(body.Errors);

        // Nothing moved: the session still works and the old password is still the password.
        AssertEx.Equal(HttpStatusCode.OK, (await ProbeAsync(client, session.AccessToken)).StatusCode);
        using var oldLogin = await LoginAsync(client, OldPassword);
        AssertEx.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
    }

    [Test]
    public async Task ChangePassword_WhenNewPasswordIsTooShort_IsRejectedByTheValidator()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);
        var session = await SignInAsync(client, OldPassword);

        // Under NodeChangePasswordRequestValidator's 12-character floor, so FastEndpoints answers its own validation
        // 400 and the request never reaches Identity.
        using var response = await ChangeAsync(client, session.AccessToken, OldPassword, "Sh0rt!");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var oldLogin = await LoginAsync(client, OldPassword);
        AssertEx.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
    }

    [Test]
    public async Task ChangePassword_WhenNewPasswordViolatesTheIdentityPolicy_IsRejectedByIdentity()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);
        var session = await SignInAsync(client, OldPassword);

        // Long enough for the validator, but all lowercase: Identity's own PasswordOptions reject it, so the failure
        // arrives as the endpoint's NodeAuthErrorResponse rather than as a FluentValidation problem.
        using var response = await ChangeAsync(client, session.AccessToken, OldPassword, "abcdefghijklmnop");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<NodeAuthErrorResponse>());
        AssertEx.NotEmpty(body.Errors);
        using var oldLogin = await LoginAsync(client, OldPassword);
        AssertEx.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
    }

    [Test]
    public async Task ChangePassword_WhenItSucceeds_ReturnsNoContentAndKillsTheCallersOwnSession()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);
        var session = await SignInAsync(client, OldPassword);

        // The bearer token works before the change...
        AssertEx.Equal(HttpStatusCode.OK, (await ProbeAsync(client, session.AccessToken)).StatusCode);

        using var response = await ChangeAsync(client, session.AccessToken, OldPassword, NewPassword);
        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertRefreshCookieCleared(response);

        // ...and is dead immediately afterwards, unexpired: the rotated security stamp no longer matches the token's.
        AssertEx.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(client, session.AccessToken)).StatusCode);

        // The refresh cookie the caller was holding cannot mint a replacement either — every active token was revoked,
        // so there is no silent path back into the session.
        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", session.RefreshCookie);
        using var refreshResponse = await client.SendAsync(refreshRequest);
        AssertEx.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
    }

    [Test]
    public async Task ChangePassword_WhenItSucceeds_OnlyTheNewPasswordLogsIn()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);
        var session = await SignInAsync(client, OldPassword);

        using var response = await ChangeAsync(client, session.AccessToken, OldPassword, NewPassword);
        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var oldLogin = await LoginAsync(client, OldPassword);
        AssertEx.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        using var newLogin = await LoginAsync(client, NewPassword);
        AssertEx.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Test]
    public async Task ChangePassword_WithoutABearerToken_IsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        await SetupAsync(client);

        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/change-password",
            new
            {
                currentPassword = OldPassword,
                newPassword = NewPassword
            });

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var oldLogin = await LoginAsync(client, OldPassword);
        AssertEx.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
    }

    /// <summary>
    ///     The endpoint carries the same throttle as setup/login/refresh. Asserted as route METADATA rather than by
    ///     driving a 429, because this module's hosts run under the <c>Testing</c> environment where
    ///     <c>Program.CreateAppAsync</c> omits <c>UseRateLimiter()</c> and the permit limit is relaxed to 10,000 — no
    ///     429 is observable here at all. The live 429 on this route is
    ///     <c>RateLimitPolicyTests.AuthPolicy_WhenWindowExhausted_Returns429WithRetryAfter</c>, in the one fixture that
    ///     really runs the middleware. The metadata is what that enforcement hangs off, and losing it is the regression
    ///     this catches.
    /// </summary>
    [Test]
    public async Task ChangePassword_CarriesTheSharedAuthRateLimitPolicy()
    {
        await using var factory = new TestServerWebAppFactory();

        var policies = factory.Services.GetRequiredService<EndpointDataSource>()
                              .Endpoints
                              .OfType<RouteEndpoint>()
                              .Where(static endpoint => endpoint.RoutePattern.RawText?.EndsWith(LocalApiRoutes.Auth.ChangePassword, StringComparison.Ordinal) == true)
                              .SelectMany(static endpoint => endpoint.Metadata.OfType<EnableRateLimitingAttribute>())
                              .Select(static attribute => attribute.PolicyName)
                              .ToArray();

        AssertEx.Contains(policies, NodeAuthRateLimits.AuthPolicy,
            "auth/change-password verifies the current password, so it must share the auth endpoints' throttle: "
            + "Operator authorization bounds who may guess, not how often.");
    }

    private static async Task<HttpResponseMessage> ChangeAsync(HttpClient client, string accessToken,
        string currentPassword, string newPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/change-password")
        {
            Content = JsonContent.Create(new
            {
                currentPassword,
                newPassword
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ProbeAsync(HttpClient client, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OperatorProbeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<Session> SignInAsync(HttpClient client, string password)
    {
        using var response = await LoginAsync(client, password);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<AccessTokenBody>());
        return new Session
        {
            AccessToken = token.AccessToken,
            RefreshCookie = GetRefreshCookie(response)
        };
    }

    private static Task<HttpResponseMessage> SetupAsync(HttpClient client)
    {
        return client.PostAsJsonAsync("/api/local/v1/auth/setup",
            new
            {
                email = Email,
                password = OldPassword
            });
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password)
    {
        return client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password
            });
    }

    private static string GetRefreshCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=", StringComparison.Ordinal))
            : null;

        return AssertEx.NotNull(setCookie).Split(separator: ';', count: 2)[0];
    }

    private static void AssertRefreshCookieCleared(HttpResponseMessage response)
    {
        var setCookieHeaders = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        AssertEx.Contains(setCookieHeaders,
            header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=;", StringComparison.Ordinal));
    }

    private sealed record Session
    {
        public required string AccessToken { get; init; }

        public required string RefreshCookie { get; init; }
    }

    private sealed record AccessTokenBody(string AccessToken, DateTime ExpiresAtUtc);
}
