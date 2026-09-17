namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The reuse-grace window in <see cref="INodeAuthService.RefreshAsync" />: a refresh token that ROTATION replaced
///     still buys a successor for a short while, so the loser of the two refreshes every document load races does not
///     get a 401 that clears the cookie and signs the operator out. The window must not extend to a token that LOGOUT,
///     a password change or a reset revoked — nothing records why a token was revoked, so these tests walk each of
///     those paths and pin that the discriminator (a live successor created at the revocation's own instant) tells them
///     apart. The clock is this host's own <see cref="ManualTimeProvider" />; the window is
///     <c>NodeAuthService.RotationGraceWindow</c>.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeAuthRefreshRotationGraceTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";

    private static readonly TimeSpan InsideTheWindow = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan PastTheWindow = TimeSpan.FromSeconds(11);

    [Test]
    public async Task Refresh_WhenARotatedTokenIsPresentedInsideTheWindow_IssuesAUsablePair()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateHost(clock);
        var login = await SetupAndLoginAsync(factory);

        var winner = await RefreshAsync(factory, login.RefreshToken);
        AssertEx.True(winner.Succeeded, "The first refresh rotates the login token.");

        clock.Advance(InsideTheWindow);
        var loser = await RefreshAsync(factory, login.RefreshToken);

        AssertEx.True(loser.Succeeded, "A token rotation replaced must still refresh inside the grace window.");
        AssertEx.NotNullOrEmpty(loser.RefreshToken);
        AssertEx.NotEqual(AssertEx.NotNull(winner.RefreshToken), AssertEx.NotNull(loser.RefreshToken));

        // "Usable", not merely non-empty: the pair the grace path returns must itself rotate like any other.
        var next = await RefreshAsync(factory, loser.RefreshToken);
        AssertEx.True(next.Succeeded, "The successor the grace path issued must be a real refresh token.");
    }

    [Test]
    public async Task Refresh_WhenARotatedTokenIsPresentedAfterTheWindow_Fails()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateHost(clock);
        var login = await SetupAndLoginAsync(factory);

        AssertEx.True((await RefreshAsync(factory, login.RefreshToken)).Succeeded);

        clock.Advance(PastTheWindow);
        var replay = await RefreshAsync(factory, login.RefreshToken);

        AssertEx.False(replay.Succeeded, "The grace window is bounded: past it, a rotated token is a replay again.");
    }

    // The hole the discriminator exists to keep shut: logout revokes through the same column rotation does.
    [Test]
    public async Task Refresh_WhenTheTokenWasRevokedByLogout_FailsInsideTheWindow()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateHost(clock);
        var login = await SetupAndLoginAsync(factory);

        await LogoutAsync(factory, login);

        clock.Advance(InsideTheWindow);
        var afterLogout = await RefreshAsync(factory, login.RefreshToken);

        AssertEx.False(afterLogout.Succeeded, "A logged-out refresh token must never be resurrected by the grace window.");
    }

    // Logout AFTER a rotation: the successor that would vouch for the rotated token is revoked too, so the rotated
    // token's grace must end with the session rather than outliving it.
    [Test]
    public async Task Refresh_WhenLogoutFollowsARotation_FailsForTheRotatedTokenInsideTheWindow()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateHost(clock);
        var login = await SetupAndLoginAsync(factory);

        var rotated = await RefreshAsync(factory, login.RefreshToken);
        AssertEx.True(rotated.Succeeded);
        await LogoutAsync(factory, rotated);

        clock.Advance(InsideTheWindow);
        var afterLogout = await RefreshAsync(factory, login.RefreshToken);

        AssertEx.False(afterLogout.Succeeded, "Logging out must also end the grace of the token rotation had just replaced.");
    }

    // The adversarial shape: logout leaves no successor, but a fresh login right afterwards creates a live token for
    // the same user. Only "created at the revocation's own instant" — not "created after it" — keeps that login from
    // vouching for the pre-logout token.
    [Test]
    public async Task Refresh_WhenLogoutIsFollowedByAFreshLogin_FailsForThePreLogoutToken()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateHost(clock);
        var login = await SetupAndLoginAsync(factory);

        await LogoutAsync(factory, login);
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertEx.True((await LoginAsync(factory)).Succeeded, "The operator signs back in.");

        var afterRelogin = await RefreshAsync(factory, login.RefreshToken);

        AssertEx.False(afterRelogin.Succeeded,
            "A fresh login is not rotation: it must not vouch for the token logout revoked, even inside the window.");
    }

    [Test]
    public async Task Refresh_WhenTheTokenIsExpired_FailsEvenThoughItWasNeverRevoked()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateHost(clock);
        var login = await SetupAndLoginAsync(factory);

        // Past RefreshTokenDays (14 by default), so the token is live-but-expired: expiry is checked before the grace
        // path can look at it at all.
        clock.Advance(TimeSpan.FromDays(15));
        var expired = await RefreshAsync(factory, login.RefreshToken);

        AssertEx.False(expired.Succeeded, "An expired refresh token fails regardless of how it was revoked.");
    }

    // The endpoint contract on the far side of the window: a genuine replay still answers 401 AND clears the cookie.
    // Its twin inside the window is NodeAuthEndpointTests.Refresh_WhenTheRotationLoserPresentsItsCookie_….
    [Test]
    public async Task RefreshEndpoint_WhenTheCookieIsReplayedAfterTheWindow_ReturnsUnauthorizedAndClearsTheCookie()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateHost(clock);
        using var client = factory.CreateClient();
        await SetupAsync(client);

        using var loginResponse = await client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password = Password
            });
        AssertEx.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var cookie = GetRefreshCookie(loginResponse);

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        firstRequest.Headers.Add("Cookie", cookie);
        using var firstResponse = await client.SendAsync(firstRequest);
        AssertEx.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        clock.Advance(PastTheWindow);
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        replayRequest.Headers.Add("Cookie", cookie);
        using var replayResponse = await client.SendAsync(replayRequest);

        AssertEx.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        AssertEx.Contains(GetSetCookieHeaders(replayResponse),
            header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=;", StringComparison.Ordinal),
            "A refresh that genuinely failed must still clear the cookie.");
    }

    private static TestServerWebAppFactory CreateHost(ManualTimeProvider clock)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            }
        };
    }

    private static async Task<NodeAuthTokenResult> SetupAndLoginAsync(TestServerWebAppFactory factory)
    {
        using (var client = factory.CreateClient())
        {
            await SetupAsync(client);
        }

        var login = await LoginAsync(factory);
        AssertEx.True(login.Succeeded, "The fixture's login must succeed before anything about refresh is asserted.");
        return login;
    }

    private static async Task SetupAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/setup",
            new
            {
                email = Email,
                password = Password
            });
        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<NodeAuthTokenResult> LoginAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                          .LoginAsync(Email, Password, CancellationToken.None);
    }

    private static async Task<NodeAuthTokenResult> RefreshAsync(TestServerWebAppFactory factory, string? refreshToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                          .RefreshAsync(refreshToken, CancellationToken.None);
    }

    /// <summary>
    ///     Logs out through the real endpoint rather than a hand-built principal, so the revoke path under test is the
    ///     one the SPA actually reaches.
    /// </summary>
    private static async Task LogoutAsync(TestServerWebAppFactory factory, NodeAuthTokenResult session)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AssertEx.NotNull(session.AccessToken));
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static string GetRefreshCookie(HttpResponseMessage response)
    {
        var setCookie = AssertEx.NotNull(GetSetCookieHeaders(response)
            .FirstOrDefault(header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=", StringComparison.Ordinal)));
        return setCookie.Split(separator: ';', count: 2)[0];
    }

    private static IReadOnlyList<string> GetSetCookieHeaders(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
    }
}
