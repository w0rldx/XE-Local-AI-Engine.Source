namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Hosting.Vault;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class NodeAuthEndpointTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";

    private const string OperatorProbeUrl = "/api/local/v1/node-settings";

    // The wire name, spelled out rather than taken from the endpoint so a rename breaks this contract test.
    private const string RefreshTokenHeader = "X-XE-Refresh-Token";

    [Test]
    public async Task AuthFlow_WhenSetupLoginRefreshAndLogout_RunSuccessfully()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        var initialStatus = await client.GetFromJsonAsync<AuthStatusResponse>("/api/local/v1/auth/status");
        AssertEx.True(AssertEx.NotNull(initialStatus).SetupRequired);
        // The same per-process id the vault-unlock host serves (VaultUnlockHostTests), so a client sees no restart between the two.
        AssertEx.Equal(NodeProcessIdentity.Generation, AssertEx.NotNull(initialStatus).ProcessGeneration);
        AssertEx.Equal(expected: 32, NodeProcessIdentity.Generation.Length);

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        using var loginResponse = await LoginAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginToken = await ReadTokenAsync(loginResponse);
        var loginRefreshCookie = GetRefreshCookie(loginResponse);

        // Any Operator-policy endpoint proves the freshly minted bearer token authenticates; GET node-settings is the
        // cheapest stable one. Nothing in the body is read — the status code IS the assertion.
        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, OperatorProbeUrl);
        probeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginToken.AccessToken);
        using var probeResponse = await client.SendAsync(probeRequest);
        AssertEx.Equal(HttpStatusCode.OK, probeResponse.StatusCode);

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", loginRefreshCookie);
        using var refreshResponse = await client.SendAsync(refreshRequest);
        AssertEx.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshedToken = await ReadTokenAsync(refreshResponse);
        var rotatedRefreshCookie = GetRefreshCookie(refreshResponse);

        AssertEx.NotEqual(loginToken.AccessToken, refreshedToken.AccessToken);
        AssertEx.NotEqual(loginRefreshCookie, rotatedRefreshCookie);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/logout");
        logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken.AccessToken);
        logoutRequest.Headers.Add("Cookie", rotatedRefreshCookie);
        using var logoutResponse = await client.SendAsync(logoutRequest);
        AssertEx.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        AssertRefreshCookieCleared(logoutResponse);
    }

    // Every document load refreshes (the SPA holds the access token in memory only), so a reload with a refresh already
    // in flight — or a second tab — presents one cookie twice. The loser must be answered with a cookie, not with the
    // 401 that CLEARS it and would wipe the winner's fresh one. Past the grace window the same replay is a 401 again:
    // NodeAuthRefreshRotationGraceTests.RefreshEndpoint_WhenTheCookieIsReplayedAfterTheWindow_….
    [Test]
    public async Task Refresh_WhenTheRotationLoserPresentsItsCookie_ReturnsOkAndSetsACookie()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        using var loginResponse = await LoginAsync(client);
        var originalRefreshCookie = GetRefreshCookie(loginResponse);

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", originalRefreshCookie);
        using var refreshResponse = await client.SendAsync(refreshRequest);
        AssertEx.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var winnerRefreshCookie = GetRefreshCookie(refreshResponse);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        replayRequest.Headers.Add("Cookie", originalRefreshCookie);
        using var replayResponse = await client.SendAsync(replayRequest);

        AssertEx.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var loserRefreshCookie = GetRefreshCookie(replayResponse);
        AssertEx.NotEqual($"{NodeAuthCookie.RefreshCookieName}=", loserRefreshCookie);
        AssertEx.NotEqual(originalRefreshCookie, loserRefreshCookie);
        AssertEx.NotEqual(winnerRefreshCookie, loserRefreshCookie);
    }

    [Test]
    public async Task Refresh_WhenCookieIsMissing_ReturnsUnauthorizedAndClearsCookie()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/local/v1/auth/refresh", content: null);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertRefreshCookieCleared(response);
    }

    // Body mode (a non-browser client): the refresh token travels in the body, never in a cookie, and cookie mode stays
    // byte-identical, so the SPA's login body never gains the field.
    [Test]
    public async Task Login_WhenReturnRefreshTokenIsSet_ReturnsTheRefreshTokenInTheBodyAndSetsNoCookie()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using (var setupResponse = await SetupAsync(client))
        {
            AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        }

        using var bodyLogin = await LoginAsync(client, returnRefreshToken: true);

        AssertEx.Equal(HttpStatusCode.OK, bodyLogin.StatusCode);
        AssertEx.Empty(GetSetCookieHeaders(bodyLogin), "Body mode must not write, or clear, any cookie.");
        var tokens = await ReadBodyTokensAsync(bodyLogin);
        AssertEx.NotNullOrEmpty(tokens.AccessToken);
        AssertEx.NotNullOrEmpty(tokens.RefreshToken);
        AssertEx.True(tokens.RefreshTokenExpiresAtUtc > tokens.ExpiresAtUtc, "The refresh token must outlive the access token.");

        using var cookieLogin = await LoginAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, cookieLogin.StatusCode);
        GetRefreshCookie(cookieLogin);
        var cookieBody = await cookieLogin.Content.ReadAsStringAsync();
        AssertEx.False(cookieBody.Contains("refreshToken", StringComparison.OrdinalIgnoreCase),
            $"Cookie mode must not carry the refresh token in the body: {cookieBody}");
    }

    [Test]
    public async Task Refresh_WhenTheHeaderCarriesTheToken_RotatesInBodyModeAndTheOldTokenDiesAfterTheGrace()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var login = await SetupAndBodyLoginAsync(client);

        using var first = await RefreshWithHeaderAsync(client, login.RefreshToken);
        AssertEx.Equal(HttpStatusCode.OK, first.StatusCode);
        AssertEx.Empty(GetSetCookieHeaders(first), "A header refresh must never set or clear a cookie.");
        var rotated = await ReadBodyTokensAsync(first);
        AssertEx.NotEqual(login.RefreshToken, rotated.RefreshToken);
        AssertEx.NotEqual(login.AccessToken, rotated.AccessToken);

        clock.Advance(TimeSpan.FromSeconds(11));
        using var replay = await RefreshWithHeaderAsync(client, login.RefreshToken);
        AssertEx.Equal(HttpStatusCode.Unauthorized, replay.StatusCode, "Past the grace window the rotated token is a replay.");
        AssertEx.Empty(GetSetCookieHeaders(replay));

        using var next = await RefreshWithHeaderAsync(client, AssertEx.NotNull(rotated.RefreshToken));
        AssertEx.Equal(HttpStatusCode.OK, next.StatusCode, "The rotated token must refresh like any other.");
        AssertEx.NotNullOrEmpty((await ReadBodyTokensAsync(next)).RefreshToken);
    }

    // A browser that also carries the header (nothing in the SPA sends it) is still answered from its cookie, and the
    // header token is not consumed.
    [Test]
    public async Task Refresh_WhenBothTheCookieAndTheHeaderAreSent_UsesTheCookieAndLeavesTheHeaderTokenUnspent()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        var bodyLogin = await SetupAndBodyLoginAsync(client);
        using var cookieLogin = await LoginAsync(client);
        var cookie = GetRefreshCookie(cookieLogin);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add(RefreshTokenHeader, bodyLogin.RefreshToken);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.NotEqual(cookie, GetRefreshCookie(response));
        AssertEx.Null((await ReadBodyTokensAsync(response)).RefreshToken, "The cookie path answers in cookie mode.");

        using var headerOnly = await RefreshWithHeaderAsync(client, bodyLogin.RefreshToken);
        AssertEx.Equal(HttpStatusCode.OK, headerOnly.StatusCode, "The ignored header token must still be live.");
    }

    // The header path's bypass table: a failing row answers the bodyless 401 and leaves the cookie alone unless one was sent.
    // An empty header value never reaches the endpoint and an empty node_rt is no cookie, so both fall to the other credential.
    [Test]
    [Arguments("header-live", HttpStatusCode.OK, "none", true)]
    [Arguments("header-empty", HttpStatusCode.Unauthorized, "cleared", false)]
    [Arguments("header-whitespace", HttpStatusCode.Unauthorized, "none", false)]
    [Arguments("header-oversized", HttpStatusCode.Unauthorized, "none", false)]
    [Arguments("header-unknown", HttpStatusCode.Unauthorized, "none", false)]
    [Arguments("header-sent-twice", HttpStatusCode.Unauthorized, "none", false)]
    [Arguments("header-access-token", HttpStatusCode.Unauthorized, "none", false)]
    [Arguments("header-rotated-past-grace", HttpStatusCode.Unauthorized, "none", false)]
    [Arguments("header-after-logout", HttpStatusCode.Unauthorized, "none", false)]
    [Arguments("header-after-password-change", HttpStatusCode.Unauthorized, "none", false)]
    [Arguments("empty-cookie-and-live-header", HttpStatusCode.OK, "none", true)]
    [Arguments("unknown-cookie-and-live-header", HttpStatusCode.Unauthorized, "cleared", false)]
    [Arguments("unlock-ticket-cookie-and-live-header", HttpStatusCode.OK, "none", true)]
    [Arguments("no-cookie-no-header", HttpStatusCode.Unauthorized, "cleared", false)]
    public async Task Refresh_HeaderAndCookieCombinations_AreDecidedCookieFirst(string scenario,
        HttpStatusCode expectedStatus,
        string expectedRefreshCookie,
        bool expectBodyRefreshToken)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var login = await SetupAndBodyLoginAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        switch (scenario)
        {
            case "header-live":
                request.Headers.Add(RefreshTokenHeader, login.RefreshToken);
                break;
            case "header-empty":
                AssertEx.True(request.Headers.TryAddWithoutValidation(RefreshTokenHeader, string.Empty));
                break;
            case "header-whitespace":
                AssertEx.True(request.Headers.TryAddWithoutValidation(RefreshTokenHeader, "   "));
                break;
            case "header-oversized":
                request.Headers.Add(RefreshTokenHeader, new string('A', 16 * 1024));
                break;
            case "header-unknown":
                request.Headers.Add(RefreshTokenHeader, Convert.ToBase64String(new byte[64]));
                break;
            case "header-sent-twice":
                request.Headers.Add(RefreshTokenHeader, [login.RefreshToken, login.RefreshToken]);
                break;
            case "header-access-token":
                request.Headers.Add(RefreshTokenHeader, login.AccessToken);
                break;
            case "header-rotated-past-grace":
                using (var rotation = await RefreshWithHeaderAsync(client, login.RefreshToken))
                {
                    AssertEx.Equal(HttpStatusCode.OK, rotation.StatusCode);
                }

                clock.Advance(TimeSpan.FromSeconds(11));
                request.Headers.Add(RefreshTokenHeader, login.RefreshToken);
                break;
            case "header-after-logout":
                await SendAuthorizedAsync(client, "/api/local/v1/auth/logout", login.AccessToken, content: null, HttpStatusCode.NoContent);
                request.Headers.Add(RefreshTokenHeader, login.RefreshToken);
                break;
            case "header-after-password-change":
                await SendAuthorizedAsync(client, "/api/local/v1/auth/change-password", login.AccessToken,
                    JsonContent.Create(new
                    {
                        currentPassword = Password,
                        newPassword = "An0ther!Password456"
                    }), HttpStatusCode.NoContent);
                request.Headers.Add(RefreshTokenHeader, login.RefreshToken);
                break;
            case "empty-cookie-and-live-header":
                request.Headers.Add("Cookie", $"{NodeAuthCookie.RefreshCookieName}=");
                request.Headers.Add(RefreshTokenHeader, login.RefreshToken);
                break;
            case "unknown-cookie-and-live-header":
                request.Headers.Add("Cookie", $"{NodeAuthCookie.RefreshCookieName}=a-refresh-token-the-node-never-issued");
                request.Headers.Add(RefreshTokenHeader, login.RefreshToken);
                break;
            case "unlock-ticket-cookie-and-live-header":
                request.Headers.Add("Cookie", $"{NodeAuthCookie.UnlockTicketCookieName}={VaultUnlockTicket.NewValue()}");
                request.Headers.Add(RefreshTokenHeader, login.RefreshToken);
                break;
            case "no-cookie-no-header":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown bypass row.");
        }

        using var response = await client.SendAsync(request);

        AssertEx.Equal(expectedStatus, response.StatusCode, scenario);
        var refreshCookies = GetSetCookieHeaders(response)
                             .Where(static header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=", StringComparison.Ordinal))
                             .ToArray();
        switch (expectedRefreshCookie)
        {
            case "none":
                AssertEx.Empty(refreshCookies, $"{scenario}: the refresh cookie must be left alone.");
                break;
            case "cleared":
                AssertEx.ContainsSingle(refreshCookies, static header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=;", StringComparison.Ordinal),
                    $"{scenario}: the cookie path's 401 clears the cookie, as before.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expectedRefreshCookie), expectedRefreshCookie, "Unknown cookie expectation.");
        }

        if (expectBodyRefreshToken)
        {
            AssertEx.NotNullOrEmpty((await ReadBodyTokensAsync(response)).RefreshToken, scenario);
        }
        else if (expectedStatus == HttpStatusCode.Unauthorized)
        {
            AssertEx.Empty(await response.Content.ReadAsStringAsync(), $"{scenario}: the 401 stays bodyless, as on the cookie path.");
        }
    }

    [Test]
    public async Task Refresh_WhenTheRefreshCookieIsUnknownAndTheUnlockTicketIsValid_IssuesAnAdminSessionOnce()
    {
        var value = VaultUnlockTicket.NewValue();
        await using var factory = CreateFactory(new VaultUnlockTicket(value, TimeProvider.System.GetUtcNow() + VaultUnlockTicket.Lifetime));
        using var client = factory.CreateClient();
        using (var setupResponse = await SetupAsync(client))
        {
            AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        }

        using (var first = await RefreshWithUnlockTicketAsync(client, value))
        {
            AssertEx.Equal(HttpStatusCode.OK, first.StatusCode);
            GetRefreshCookie(first);
            AssertUnlockTicketCleared(first);

            using var probeRequest = new HttpRequestMessage(HttpMethod.Get, OperatorProbeUrl);
            probeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await ReadTokenAsync(first)).AccessToken);
            using var probeResponse = await client.SendAsync(probeRequest);
            AssertEx.Equal(HttpStatusCode.OK, probeResponse.StatusCode, "The ticket's session must be the admin's.");
        }

        using var replay = await RefreshWithUnlockTicketAsync(client, value);
        AssertEx.Equal(HttpStatusCode.Unauthorized, replay.StatusCode, "The unlock ticket is single-use.");
        AssertRefreshCookieKept(replay);
        AssertUnlockTicketCleared(replay);
    }

    [Test]
    public async Task Refresh_WhenTheUnlockTicketWasAlreadySpent_ReturnsUnauthorizedWithoutClearingTheRefreshCookie()
    {
        var value = VaultUnlockTicket.NewValue();
        var ticket = new VaultUnlockTicket(value, TimeProvider.System.GetUtcNow() + VaultUnlockTicket.Lifetime);
        AssertEx.True(ticket.TryConsume(value, TimeProvider.System.GetUtcNow()), "The first tab spends the ticket.");
        await using var factory = CreateFactory(ticket);
        using var client = factory.CreateClient();
        using (var setupResponse = await SetupAsync(client))
        {
            AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        }

        using var response = await RefreshWithUnlockTicketAsync(client, value);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertRefreshCookieKept(response);
        AssertUnlockTicketCleared(response);
    }

    [Test]
    public async Task Refresh_WhenTheUnlockTicketHasExpired_ReturnsUnauthorized()
    {
        var value = VaultUnlockTicket.NewValue();
        await using var factory = CreateFactory(new VaultUnlockTicket(value, TimeProvider.System.GetUtcNow() - TimeSpan.FromSeconds(1)));
        using var client = factory.CreateClient();
        using (var setupResponse = await SetupAsync(client))
        {
            AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        }

        using var response = await RefreshWithUnlockTicketAsync(client, value);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertRefreshCookieKept(response);
        AssertUnlockTicketCleared(response);
    }

    [Test]
    public async Task Refresh_WhenTheUnlockTicketDoesNotMatch_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory(new VaultUnlockTicket(VaultUnlockTicket.NewValue(), TimeProvider.System.GetUtcNow() + VaultUnlockTicket.Lifetime));
        using var client = factory.CreateClient();
        using (var setupResponse = await SetupAsync(client))
        {
            AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        }

        using var response = await RefreshWithUnlockTicketAsync(client, VaultUnlockTicket.NewValue());

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertRefreshCookieKept(response);
        AssertUnlockTicketCleared(response);
    }

    [Test]
    public async Task Setup_WhenAlreadyInitialized_ReturnsConflict()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        using var duplicateSetupResponse = await SetupAsync(client, "other@example.test");

        AssertEx.Equal(HttpStatusCode.Conflict, duplicateSetupResponse.StatusCode);
    }

    [Test]
    public async Task Setup_WhenConcurrentRequestsRace_CreatesSingleAdmin()
    {
        await using var factory = new TestServerWebAppFactory();
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var responses = await Task.WhenAll(SetupAsync(firstClient, "first@example.test"),
            SetupAsync(secondClient, "second@example.test"));

        try
        {
            AssertEx.Equal(expected: 1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
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

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password = "wrong-password"
            });

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertRefreshCookieCleared(response);
    }

    // Pins the deliberate shape behind the OpenAPI spec listing only `password` as required on the login request:
    // `NodeLoginRequest.Email` is nullable on purpose, and `NodeAuthService.ResolveLoginUserAsync` resolves the single
    // SetupCompleted user when no email is supplied.
    /// <summary>A non-ASCII address the setup form accepts must set the node up and sign in, not fail Identity's user-name rule.</summary>
    [Test]
    public async Task SetupAndLogin_WithANonAsciiEmail_Succeed()
    {
        const string email = "jürgen-ß@localhost.test";
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client, email);
        AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode, await setupResponse.Content.ReadAsStringAsync());

        using var loginResponse = await client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                email,
                password = Password
            });
        AssertEx.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var token = await ReadTokenAsync(loginResponse);

        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, OperatorProbeUrl);
        probeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var probeResponse = await client.SendAsync(probeRequest);
        AssertEx.Equal(HttpStatusCode.OK, probeResponse.StatusCode);
    }

    [Test]
    public async Task Login_WhenEmailIsOmitted_ResolvesTheSingleCompletedAdmin()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password = Password
            });

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await ReadTokenAsync(response);
        AssertEx.NotEmpty(token.AccessToken);
    }

    [Test]
    public async Task Setup_WhenItSucceeds_MarksTheExternalAccessProfilePending()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        var stored = await factory.Services.GetRequiredService<INodeSettingsStore>().LoadAsync();
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, stored.ExternalAccessProfile);
    }

    // The sandbox security profile is stamped in the same write (ADR 0020): "pending" is what routes the operator to the chooser, and it
    // keeps the boot backfill, which would decide "low", away from a node whose operator is about to choose.
    [Test]
    public async Task Setup_WhenItSucceeds_MarksTheSandboxSecurityProfilePending()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        var stored = await factory.Services.GetRequiredService<INodeSettingsStore>().LoadAsync();
        AssertEx.Equal(StoredNodeSettings.SandboxSecurityProfilePending, stored.SandboxSecurityProfile);
    }

    // R5b's ordering, from the failure side: the profile write lands, the operation is cancelled, and the identity
    // transaction never commits. The orphan this leaves is the harmless one — a pending profile with no administrator —
    // and both halves are asserted, because the whole point of writing before the commit is that the OTHER orphan
    // ("administrator exists, profile null", which the boot backfill would decide as recommended) is unreachable.
    [Test]
    public async Task Setup_WhenCancelledBetweenTheTwoWrites_LeavesSetupRequiredWithAPendingProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-node-auth-pending", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            using var store = new NodeSettingsStore(new FakeNodeDataDirectory(root), NullLogger<NodeSettingsStore>.Instance);
            var seam = new CancelTheFirstWriteSettingsStore(store);
            await using var factory = CreateFactory(seam);

            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => RunSetupAsync(factory));

            AssertEx.Equal(expected: 1, seam.CancelledWrites, "The seam must have interrupted exactly the profile write.");
            AssertEx.True(await SetupRequiredAsync(factory),
                "A cancelled setup must roll the identity transaction back, leaving no administrator.");
            var stored = await store.LoadAsync();
            AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, stored.ExternalAccessProfile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The `is null` guard in NodeAuthService.SetupAsync: a retry after the orphaned write above must not clobber the
    // profile it finds, or a decided node could be reset to pending by a second setup attempt.
    [Test]
    public async Task Setup_RetriedAfterAPendingProfileWasWritten_SucceedsAndKeepsPending()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-node-auth-pending-retry", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            using var store = new NodeSettingsStore(new FakeNodeDataDirectory(root), NullLogger<NodeSettingsStore>.Instance);
            var seam = new CancelTheFirstWriteSettingsStore(store);
            await using var factory = CreateFactory(seam);

            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => RunSetupAsync(factory));

            var retry = await RunSetupAsync(factory);

            AssertEx.True(retry.Succeeded, "The retried setup must succeed once the seam stops interrupting.");
            AssertEx.False(await SetupRequiredAsync(factory));
            var stored = await store.LoadAsync();
            AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, stored.ExternalAccessProfile);
            AssertEx.Equal(StoredNodeSettings.SandboxSecurityProfilePending, stored.SandboxSecurityProfile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     Drives <see cref="INodeAuthService.SetupAsync" /> directly rather than through the endpoint, because the
    ///     cancellation these tests inject is the thing under test and the endpoint would render it as a status code.
    /// </summary>
    private static async Task<NodeSetupResult> RunSetupAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                          .SetupAsync(Email, Password, CancellationToken.None);
    }

    private static async Task<bool> SetupRequiredAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var status = await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                                .GetStatusAsync(new ClaimsPrincipal(), CancellationToken.None);
        return status.SetupRequired;
    }

    private static TestServerWebAppFactory CreateFactory(INodeSettingsStore nodeSettingsStore)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton(nodeSettingsStore);
            }
        };
    }

    /// <summary>The real host as a pre-host password unlock leaves it: the ticket registered, the browser holding a dead refresh cookie.</summary>
    private static TestServerWebAppFactory CreateFactory(VaultUnlockTicket unlockTicket)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services => services.AddSingleton(unlockTicket)
        };
    }

    private static async Task<HttpResponseMessage> RefreshWithUnlockTicketAsync(HttpClient client, string unlockTicket)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        request.Headers.Add("Cookie", $"{NodeAuthCookie.RefreshCookieName}=a-refresh-token-the-node-never-issued; {NodeAuthCookie.UnlockTicketCookieName}={unlockTicket}");
        return await client.SendAsync(request);
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

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, bool returnRefreshToken)
    {
        return client.PostAsJsonAsync("/api/local/v1/auth/login",
            new
            {
                password = Password,
                returnRefreshToken
            });
    }

    private static async Task<BodyTokenPair> SetupAndBodyLoginAsync(HttpClient client)
    {
        using (var setupResponse = await SetupAsync(client))
        {
            AssertEx.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        }

        using var loginResponse = await LoginAsync(client, returnRefreshToken: true);
        AssertEx.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var tokens = await ReadBodyTokensAsync(loginResponse);
        return new BodyTokenPair
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = AssertEx.NotNull(tokens.RefreshToken, "Body-mode login must return the refresh token.")
        };
    }

    private static async Task<HttpResponseMessage> RefreshWithHeaderAsync(HttpClient client, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/auth/refresh");
        request.Headers.Add(RefreshTokenHeader, refreshToken);
        return await client.SendAsync(request);
    }

    private static async Task SendAuthorizedAsync(HttpClient client, string url, string accessToken, HttpContent? content, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = content;
        using var response = await client.SendAsync(request);
        AssertEx.Equal(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<BodyTokenResponse> ReadBodyTokensAsync(HttpResponseMessage response)
    {
        return AssertEx.NotNull(await response.Content.ReadFromJsonAsync<BodyTokenResponse>());
    }

    private static TestServerWebAppFactory CreateFactory(ManualTimeProvider clock)
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

    private static async Task<AuthTokenResponse> ReadTokenAsync(HttpResponseMessage response)
    {
        var token = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
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

    /// <summary>A stale tab's 401 that lands after the first tab's 200 must not delete the refresh cookie that 200 set.</summary>
    private static void AssertRefreshCookieKept(HttpResponseMessage response)
    {
        AssertEx.Empty(GetSetCookieHeaders(response).Where(static header => header.StartsWith($"{NodeAuthCookie.RefreshCookieName}=", StringComparison.Ordinal)),
            "A refresh that carried an unconsumable unlock ticket must not touch the refresh cookie.");
    }

    private static void AssertUnlockTicketCleared(HttpResponseMessage response)
    {
        AssertEx.Contains(GetSetCookieHeaders(response), header => header.StartsWith($"{NodeAuthCookie.UnlockTicketCookieName}=;", StringComparison.Ordinal),
            "Every refresh that carries the unlock ticket must clear it.");
    }

    private static IReadOnlyList<string> GetSetCookieHeaders(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
    }

    /// <summary>
    ///     Decorates the REAL store and interrupts only the FIRST write: the mutation is persisted, then the caller is
    ///     cancelled. That is the window R5b's ordering is chosen for — the profile is already durable while the
    ///     identity transaction has not committed — and leaving later writes alone is what lets a retry run against a
    ///     node that genuinely holds the orphaned value.
    /// </summary>
    private sealed class CancelTheFirstWriteSettingsStore : INodeSettingsStore
    {
        private readonly INodeSettingsStore _inner;

        public CancelTheFirstWriteSettingsStore(INodeSettingsStore inner)
        {
            _inner = inner;
        }

        public int CancelledWrites { get; private set; }

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return _inner.LoadAsync(cancellationToken);
        }

        public Task<StoredNodeSettings?> LoadStrictAsync(CancellationToken cancellationToken = default)
        {
            return _inner.LoadStrictAsync(cancellationToken);
        }

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            return _inner.Load(cancellationToken);
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            return _inner.SaveAsync(settings, cancellationToken);
        }

        public async Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate,
            CancellationToken cancellationToken = default)
        {
            var persisted = await _inner.UpdateAsync(mutate, cancellationToken);
            if (CancelledWrites > 0)
            {
                return persisted;
            }

            CancelledWrites++;
            throw new OperationCanceledException("The settings write landed and the caller was then cancelled.");
        }
    }

    private sealed record AuthStatusResponse(bool SetupRequired, bool Authenticated, string? ProcessGeneration);

    private sealed record AuthTokenResponse(string AccessToken, DateTime ExpiresAtUtc);

    internal sealed class BodyTokenResponse
    {
        public required string AccessToken { get; init; }

        public required DateTime ExpiresAtUtc { get; init; }

        public string? RefreshToken { get; init; }

        public DateTime? RefreshTokenExpiresAtUtc { get; init; }
    }

    private sealed class BodyTokenPair
    {
        public required string AccessToken { get; init; }

        public required string RefreshToken { get; init; }
    }
}
