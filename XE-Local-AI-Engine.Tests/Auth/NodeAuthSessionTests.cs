namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Per-session sign-out: logout and the sessions list end exactly one sign-in at once, "sign out everywhere" ends all.</summary>
/// <remarks>
///     Every login is a session its access tokens name in <c>sid</c>, and refresh keeps a chain in its session. Tokens
///     come from the real service and requests go through the real JWT pipeline.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class NodeAuthSessionTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";
    internal const string SessionsRoute = "/api/local/v1/auth/sessions";

    [Test]
    public async Task Logout_OfOneSession_RejectsItsAccessTokenAtOnceAndLeavesTheOtherWorking()
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var first = await SetupAndLoginAsync(factory);
        var second = await LoginAsync(factory);
        AssertEx.Equal(HttpStatusCode.OK, await GetSessionsStatusAsync(factory, first), "The first session works before logout.");

        using var client = factory.CreateClient();
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/logout");
        logout.Headers.Authorization = Bearer(first);
        using var logoutResponse = await client.SendAsync(logout);
        AssertEx.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        AssertEx.Equal(HttpStatusCode.Unauthorized, await GetSessionsStatusAsync(factory, first),
            "The logged-out session's access token must be rejected at once, not at expiry.");
        AssertEx.False((await RefreshAsync(factory, first.RefreshToken)).Succeeded, "The logged-out session's refresh token is revoked.");

        var remaining = await ListSessionsAsync(factory, second);
        AssertEx.ContainsSingle(remaining, session => session.Current, "Only the other session is left, flagged as the caller's.");
        AssertEx.Equal(1, remaining.Count);
        AssertEx.True((await RefreshAsync(factory, second.RefreshToken)).Succeeded, "The other session keeps refreshing.");
    }

    [Test]
    public async Task RevokeAll_RejectsEveryAccessTokenIncludingTheCallersAndClearsTheCookie()
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var first = await SetupAndLoginAsync(factory);
        var second = await LoginAsync(factory);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionsRoute}/revoke-all");
        request.Headers.Authorization = Bearer(first);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertEx.Contains(SetCookieHeaders(response),
            header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=;", StringComparison.Ordinal),
            "Signing out everywhere includes this browser, so its refresh cookie is cleared.");
        AssertEx.Equal(HttpStatusCode.Unauthorized, await GetSessionsStatusAsync(factory, first), "The caller's own token is rejected.");
        AssertEx.Equal(HttpStatusCode.Unauthorized, await GetSessionsStatusAsync(factory, second), "Every other session's token is rejected.");
        AssertEx.False((await RefreshAsync(factory, first.RefreshToken)).Succeeded);
        AssertEx.False((await RefreshAsync(factory, second.RefreshToken)).Succeeded);
    }

    [Test]
    public async Task RevokeSession_OfAnotherOwnSession_EndsOnlyThatSession()
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var first = await SetupAndLoginAsync(factory);
        var second = await LoginAsync(factory);

        AssertEx.Equal(HttpStatusCode.NoContent, await DeleteSessionAsync(factory, first, SessionId(second)));

        AssertEx.Equal(HttpStatusCode.Unauthorized, await GetSessionsStatusAsync(factory, second), "The revoked session's token is rejected.");
        AssertEx.Equal(HttpStatusCode.OK, await GetSessionsStatusAsync(factory, first), "The caller's own session keeps working.");
        AssertEx.False((await RefreshAsync(factory, second.RefreshToken)).Succeeded);
    }

    [Test]
    public async Task RevokeSession_OfAnotherUsersOrAnUnknownSession_Returns404AndRevokesNothing()
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var admin = await SetupAndLoginAsync(factory);
        var foreignSessionId = await SeedForeignSessionAsync(factory);

        AssertEx.Equal(HttpStatusCode.NotFound, await DeleteSessionAsync(factory, admin, foreignSessionId));
        AssertEx.Equal(HttpStatusCode.NotFound, await DeleteSessionAsync(factory, admin, Guid.NewGuid().ToString()));

        await using var scope = factory.Services.CreateAsyncScope();
        var foreign = await scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>()
                                 .AuthSessions.AsNoTracking().SingleAsync(session => session.Id == foreignSessionId);
        AssertEx.Null(foreign.RevokedAtUtc, "Another user's session must never be revoked through this endpoint.");
    }

    [Test]
    public async Task Refresh_ThroughRotationAndTheGraceWindow_KeepsTheSessionId()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateHost(clock);
        var login = await SetupAndLoginAsync(factory);
        var sessionId = SessionId(login);

        var rotated = await RefreshAsync(factory, login.RefreshToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        var graced = await RefreshAsync(factory, login.RefreshToken);

        AssertEx.True(rotated.Succeeded && graced.Succeeded, "Rotation and the grace refresh both succeed.");
        AssertEx.Equal(sessionId, SessionId(rotated), "Rotation keeps the access token in its session.");
        AssertEx.Equal(sessionId, SessionId(graced), "The grace pair joins the live head's session.");

        var sessions = await ListSessionsAsync(factory, graced);
        AssertEx.Equal(1, sessions.Count, "Refreshing never starts a second session.");
        AssertEx.Equal(clock.GetUtcNow().UtcDateTime, sessions[0].LastUsedAtUtc, "A refresh bumps the session's last use.");
    }

    [Test]
    public async Task Refresh_OfATokenIssuedBeforeSessionsExisted_StartsASessionLabelledByTheClient()
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        await SetupAndLoginAsync(factory);
        var legacyRefreshToken = await SeedLegacyRefreshTokenAsync(factory);

        NodeAuthTokenResult refreshed;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            refreshed = await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                                   .RefreshAsync(legacyRefreshToken, "LegacyClient/1.0", CancellationToken.None);
        }

        AssertEx.True(refreshed.Succeeded, "A pre-session refresh token still refreshes after the upgrade.");
        var sessionId = SessionId(refreshed);
        var sessions = await ListSessionsAsync(factory, refreshed);
        AssertEx.ContainsSingle(sessions,
            session => session.Id == sessionId && session.Current && session.ClientLabel == "LegacyClient/1.0",
            "The legacy chain now lives in a session named after the refreshing client.");
    }

    [Test]
    public async Task AccessTokenWithoutSessionId_StaysValidUntilExpiry()
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        await SetupAndLoginAsync(factory);

        var legacyAccessToken = await MintLegacyAccessTokenAsync(factory);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SessionsRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyAccessToken);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "A token minted before the upgrade has no sid and must keep working.");
    }

    [Test]
    public async Task Logout_WithALegacyCookieARefreshAlreadyRotated_EndsTheSuccessorsSession()
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        await SetupAndLoginAsync(factory);
        var legacyRefreshToken = await SeedLegacyRefreshTokenAsync(factory);
        var legacyAccess = await MintLegacyAccessTokenAsync(factory);
        var rotated = await RefreshAsync(factory, legacyRefreshToken);
        AssertEx.True(rotated.Succeeded, "A concurrent refresh rotated the legacy token into a new session first.");
        var sessionId = SessionId(rotated);

        using var client = factory.CreateClient();
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyAccess);
        logout.Headers.Add("Cookie", $"{NodeAuthCookie.RefreshCookieName}={Uri.EscapeDataString(legacyRefreshToken)}");
        using var logoutResponse = await client.SendAsync(logout);
        AssertEx.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        AssertEx.False((await RefreshAsync(factory, legacyRefreshToken)).Succeeded,
            "The logged-out cookie must not be revived through the grace window by its successor.");
        AssertEx.False((await RefreshAsync(factory, rotated.RefreshToken)).Succeeded, "The successor's refresh token is revoked.");
        AssertEx.Equal(HttpStatusCode.Unauthorized, await GetSessionsStatusAsync(factory, rotated),
            "An access token naming the successor's session is rejected.");

        await using var scope = factory.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>()
                                 .AuthSessions.AsNoTracking().SingleAsync(candidate => candidate.Id == sessionId);
        AssertEx.True(session.RevokedAtUtc is not null, "The session the rotation started is ended by the logout.");
    }

    [Test]
    public async Task Logout_WithALegacyCookie_WhenARefreshRacesBetweenItsReadAndItsRevoke_LeavesNoLiveSuccessor()
    {
        var hook = new StrongBox<Func<bool, Task>?>();
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow), hook);
        var fixtureSessionId = SessionId(await SetupAndLoginAsync(factory));
        var legacyRefreshToken = await SeedLegacyRefreshTokenAsync(factory);
        var legacyAccess = await MintLegacyAccessTokenAsync(factory);

        // Right after logout reads the cookie's token, a refresh of it starts on another thread. Logout holding the write
        // lock makes it queue behind the commit; otherwise it commits R0 -> R1 + a new session before logout writes.
        Task<NodeAuthTokenResult>? competingRefresh = null;
        hook.Value = async logoutHoldsTransaction =>
        {
            competingRefresh = Task.Run(() => RefreshAsync(factory, legacyRefreshToken));
            if (!logoutHoldsTransaction)
            {
                await competingRefresh;
            }
        };

        using var client = factory.CreateClient();
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyAccess);
        logout.Headers.Add("Cookie", $"{NodeAuthCookie.RefreshCookieName}={Uri.EscapeDataString(legacyRefreshToken)}");
        using var logoutResponse = await client.SendAsync(logout);
        AssertEx.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        var competing = await AssertEx.NotNull(competingRefresh, "The hook must have run on logout's read of the cookie's token.");

        AssertEx.False((await RefreshAsync(factory, legacyRefreshToken)).Succeeded, "The logged-out cookie stays dead.");
        AssertEx.False((await RefreshAsync(factory, competing.RefreshToken)).Succeeded, "Whatever the racing refresh issued is dead too.");
        await using var scope = factory.Services.CreateAsyncScope();
        var liveSessionIds = await scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>()
                                        .AuthSessions.AsNoTracking()
                                        .Where(session => session.RevokedAtUtc == null)
                                        .Select(session => session.Id)
                                        .ToListAsync();
        AssertEx.Equal(fixtureSessionId, string.Join(",", liveSessionIds), "No session the racing refresh started may outlive the logout.");
    }

    [Test]
    [Arguments(SessionsRoute + "/revoke-all")]
    [Arguments("/api/local/v1/auth/change-password")]
    public async Task RevokeEverySession_WhenALegacyRefreshRacesBetweenItsReadAndItsWrite_LeavesNothingLive(string route)
    {
        var hook = new StrongBox<Func<bool, Task>?>();
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow), refreshTokenCommandHook: hook);
        var caller = await SetupAndLoginAsync(factory);
        var legacyRefreshToken = await SeedLegacyRefreshTokenAsync(factory);

        // Once the revoke has touched tokens and sessions, a refresh of the pre-session token runs on another thread.
        // Inside a transaction it queues behind the commit; otherwise it commits before the revoke goes on.
        Task<NodeAuthTokenResult>? competingRefresh = null;
        hook.Value = async revokeHoldsTransaction =>
        {
            competingRefresh = Task.Run(() => RefreshAsync(factory, legacyRefreshToken));
            if (!revokeHoldsTransaction)
            {
                await competingRefresh;
            }
        };

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(new
            {
                currentPassword = Password,
                newPassword = Password + "-next"
            })
        };
        request.Headers.Authorization = Bearer(caller);
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var competing = await AssertEx.NotNull(competingRefresh, "The hook must have run on the revoke's refresh-token command.");

        AssertEx.False((await RefreshAsync(factory, legacyRefreshToken)).Succeeded, "The pre-session token is revoked.");
        AssertEx.False((await RefreshAsync(factory, competing.RefreshToken)).Succeeded, "Whatever the racing refresh issued is revoked too.");
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>();
        AssertEx.Empty(await dbContext.AuthSessions.AsNoTracking().Where(session => session.RevokedAtUtc == null).ToListAsync(),
            "No session, including one the racing refresh started, may outlive revoking every session.");
        AssertEx.Empty(await dbContext.RefreshTokens.AsNoTracking().Where(token => token.RevokedAtUtc == null).ToListAsync(),
            "No refresh token may outlive revoking every session.");
    }

    [Test]
    public async Task RevokeAll_WhenTheStampWriteLosesOneConcurrencyRace_RetriesAndRejectsPreUpgradeTokens()
    {
        var conflicts = new StrongBox<int>();
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow), userRowConflicts: conflicts);
        var caller = await SetupAndLoginAsync(factory);
        var legacyAccessToken = await MintLegacyAccessTokenAsync(factory);
        var stampBefore = await ReadSecurityStampAsync(factory);
        conflicts.Value = 1;

        using var response = await PostRevokeAllAsync(factory, caller);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertEx.True(conflicts.Value < 1, "The forced conflict hit the stamp write, so the rotation below came from a retry.");
        AssertEx.NotEqual(stampBefore, await ReadSecurityStampAsync(factory), "The retry must still rotate the security stamp.");
        using var client = factory.CreateClient();
        using var probe = new HttpRequestMessage(HttpMethod.Get, SessionsRoute);
        probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyAccessToken);
        using var probeResponse = await client.SendAsync(probe);
        AssertEx.Equal(HttpStatusCode.Unauthorized, probeResponse.StatusCode, "A pre-upgrade access token carries no sid; only the stamp ends it.");
    }

    [Test]
    public async Task RevokeAll_WhenTheStampWriteKeepsLosingTheRace_DoesNotReportSuccess()
    {
        var conflicts = new StrongBox<int>();
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow), userRowConflicts: conflicts);
        var caller = await SetupAndLoginAsync(factory);
        var stampBefore = await ReadSecurityStampAsync(factory);
        conflicts.Value = int.MaxValue;

        using var response = await PostRevokeAllAsync(factory, caller);

        AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode,
            "A stamp that could not be rotated must not be reported as everyone signed out.");
        AssertEx.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertEx.Equal(stampBefore, await ReadSecurityStampAsync(factory));
    }

    [Test]
    public async Task LoginEndpoint_StoresTheUserAgentAsTheSessionLabel()
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        await SetupAndLoginAsync(factory);

        using var client = factory.CreateClient();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/login")
        {
            Content = JsonContent.Create(new
            {
                password = Password
            })
        };
        login.Headers.UserAgent.ParseAdd("SessionTestBrowser/2.0");
        using var loginResponse = await client.SendAsync(login);
        AssertEx.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var accessToken = AssertEx.NotNull(await loginResponse.Content.ReadFromJsonAsync<NodeAccessTokenResponse>()).AccessToken;

        using var list = new HttpRequestMessage(HttpMethod.Get, SessionsRoute);
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var listResponse = await client.SendAsync(list);
        var sessions = AssertEx.NotNull(await listResponse.Content.ReadFromJsonAsync<List<NodeAuthSessionResponse>>());

        AssertEx.ContainsSingle(sessions, session => session.Current && session.ClientLabel == "SessionTestBrowser/2.0");
        AssertEx.Equal(2, sessions.Count, "The fixture's own login is listed beside it.");
        AssertEx.True(sessions.All(session => session.CreatedAtUtc.Kind == DateTimeKind.Utc && session.LastUsedAtUtc.Kind == DateTimeKind.Utc),
            "The instants go on the wire as UTC, or a browser reads them as local time.");
    }

    [Test]
    [Arguments("GET", SessionsRoute)]
    [Arguments("DELETE", SessionsRoute + "/some-session")]
    [Arguments("POST", SessionsRoute + "/revoke-all")]
    public async Task SessionEndpoints_WithoutAToken_Return401(string method, string route)
    {
        await using var factory = CreateHost(new ManualTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static TestServerWebAppFactory CreateHost(ManualTimeProvider clock,
        StrongBox<Func<bool, Task>?>? refreshTokenReadHook = null,
        StrongBox<Func<bool, Task>?>? refreshTokenCommandHook = null,
        StrongBox<int>? userRowConflicts = null)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                if (userRowConflicts is not null)
                {
                    services.ConfigureDbContext<NodeIdentityDbContext>(options =>
                        options.AddInterceptors(new UserRowConflictInterceptor(userRowConflicts)));
                }

                if (refreshTokenCommandHook is not null)
                {
                    services.ConfigureDbContext<NodeIdentityDbContext>(options =>
                        options.AddInterceptors(new RefreshTokenCommandHookInterceptor(refreshTokenCommandHook)));
                }

                if (refreshTokenReadHook is not null)
                {
                    services.RemoveAll<INodeIdentityStore>();
                    services.AddScoped<INodeIdentityStore>(provider =>
                    {
                        var dbContext = provider.GetRequiredService<NodeIdentityDbContext>();
                        return new RefreshTokenReadHookIdentityStore(new NodeIdentityStore(dbContext), dbContext, refreshTokenReadHook);
                    });
                }
            }
        };
    }

    private static async Task<NodeAuthTokenResult> SetupAndLoginAsync(TestServerWebAppFactory factory)
    {
        using (var client = factory.CreateClient())
        {
            using var response = await client.PostAsJsonAsync("/api/local/v1/auth/setup",
                new
                {
                    email = Email,
                    password = Password
                });
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var login = await LoginAsync(factory);
        AssertEx.True(login.Succeeded, "The fixture's login must succeed before anything about sessions is asserted.");
        return login;
    }

    private static async Task<NodeAuthTokenResult> LoginAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                          .LoginAsync(Email, Password, clientLabel: null, CancellationToken.None);
    }

    private static async Task<NodeAuthTokenResult> RefreshAsync(TestServerWebAppFactory factory, string? refreshToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                          .RefreshAsync(refreshToken, clientLabel: null, CancellationToken.None);
    }

    private static async Task<HttpStatusCode> GetSessionsStatusAsync(TestServerWebAppFactory factory, NodeAuthTokenResult session)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SessionsRoute);
        request.Headers.Authorization = Bearer(session);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<List<NodeAuthSessionResponse>> ListSessionsAsync(TestServerWebAppFactory factory, NodeAuthTokenResult session)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SessionsRoute);
        request.Headers.Authorization = Bearer(session);
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<List<NodeAuthSessionResponse>>());
    }

    private static async Task<HttpStatusCode> DeleteSessionAsync(TestServerWebAppFactory factory, NodeAuthTokenResult caller, string sessionId)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{SessionsRoute}/{sessionId}");
        request.Headers.Authorization = Bearer(caller);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<string> SeedForeignSessionAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var other = new NodeUser
        {
            UserName = "other@example.test",
            Email = "other@example.test"
        };
        var created = await scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>().CreateAsync(other, Password);
        AssertEx.True(created.Succeeded, "The second user must exist before its session can be seeded.");

        var session = new NodeAuthSession
        {
            UserId = other.Id,
            CreatedAtUtc = DateTime.UtcNow,
            LastUsedAtUtc = DateTime.UtcNow
        };
        await scope.ServiceProvider.GetRequiredService<INodeIdentityStore>().AddSessionAsync(session, CancellationToken.None);
        return session.Id;
    }

    /// <summary>Stores a live refresh token with no session, as a node upgraded from before sessions holds one.</summary>
    private static async Task<string> SeedLegacyRefreshTokenAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<INodeTokenService>();
        var identity = scope.ServiceProvider.GetRequiredService<INodeIdentityStore>();
        var admin = AssertEx.NotNull(await identity.FindCompletedSetupUserAsync(CancellationToken.None));
        var raw = tokens.CreateRefreshTokenRaw();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

        await identity.AddRefreshTokenAsync(new NodeRefreshToken
        {
            UserId = admin.Id,
            TokenHash = tokens.HashRefreshToken(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(1)
        }, CancellationToken.None);
        return raw;
    }

    private static async Task<HttpResponseMessage> PostRevokeAllAsync(TestServerWebAppFactory factory, NodeAuthTokenResult caller)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionsRoute}/revoke-all");
        request.Headers.Authorization = Bearer(caller);
        return await client.SendAsync(request);
    }

    private static async Task<string> ReadSecurityStampAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>()
                                          .Users.AsNoTracking().Select(user => user.SecurityStamp).SingleAsync());
    }

    /// <summary>Mints an access token without <c>sid</c>, as one issued before the upgrade.</summary>
    private static async Task<string> MintLegacyAccessTokenAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<INodeIdentityStore>()
                                                .FindCompletedSetupUserAsync(CancellationToken.None));
        return scope.ServiceProvider.GetRequiredService<INodeTokenService>()
                    .CreateAccessToken(admin, [NodeAuthorizationPolicies.AdminRole]).AccessToken;
    }

    private static string SessionId(NodeAuthTokenResult result)
    {
        var sessionId = new JsonWebToken(AssertEx.NotNull(result.AccessToken)).GetClaim(JwtRegisteredClaimNames.Sid).Value;
        AssertEx.NotNullOrEmpty(sessionId, "Every access token issued by sign-in or refresh names its session.");
        return sessionId;
    }

    private static AuthenticationHeaderValue Bearer(NodeAuthTokenResult session) =>
        new("Bearer", AssertEx.NotNull(session.AccessToken));

    private static IReadOnlyList<string> SetCookieHeaders(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
}
