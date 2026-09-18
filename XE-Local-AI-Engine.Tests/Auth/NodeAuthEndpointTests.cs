namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class NodeAuthEndpointTests
{
    private const string Email = "admin@example.test";
    private const string Password = "Str0ng!Password123";

    [Test]
    public async Task AuthFlow_WhenSetupLoginRefreshAndLogout_RunSuccessfully()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        var initialStatus = await client.GetFromJsonAsync<AuthStatusResponse>("/api/local/v1/auth/status");
        AssertEx.True(AssertEx.NotNull(initialStatus).SetupRequired);

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

        using var loginResponse = await LoginAsync(client);
        AssertEx.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginToken = await ReadTokenAsync(loginResponse);
        var loginRefreshCookie = GetRefreshCookie(loginResponse);

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/local/v1/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginToken.AccessToken);
        using var meResponse = await client.SendAsync(meRequest);
        AssertEx.Equal(HttpStatusCode.OK, meResponse.StatusCode);

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
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);
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

    [Test]
    public async Task Setup_WhenAlreadyInitialized_ReturnsConflict()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

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

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

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
    [Test]
    public async Task Login_WhenEmailIsOmitted_ResolvesTheSingleCompletedAdmin()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var setupResponse = await SetupAsync(client);
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

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
        AssertEx.Equal(HttpStatusCode.NoContent, setupResponse.StatusCode);

        var stored = await factory.Services.GetRequiredService<INodeSettingsStore>().LoadAsync();
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, stored.ExternalAccessProfile);
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

    private sealed record AuthStatusResponse(bool SetupRequired, bool Authenticated);

    private sealed record AuthTokenResponse(string AccessToken, DateTime ExpiresAtUtc);
}
