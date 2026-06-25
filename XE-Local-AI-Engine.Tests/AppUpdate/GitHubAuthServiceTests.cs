namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the GitHub device-flow service: start parses the user/device codes, poll transitions pending → authorized
///     (persisting the token) and maps denied/expired, sign-out attempts a best-effort server-side revoke then clears
///     locally (local deletion is the guaranteed effect), and token material is never logged. The device-flow HTTP
///     endpoints are mocked — no real network.
/// </summary>
public sealed class GitHubAuthServiceTests : IDisposable
{
    private const string ClientId = "Iv1.testclientid";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Test]
    public async Task Start_ReturnsUserCodeAndInterval()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["device_code"] = "dev-code-abc",
            ["user_code"] = "WDJB-MJHT",
            ["verification_uri"] = "https://github.com/login/device",
            ["expires_in"] = 900,
            ["interval"] = 5
        }));
        var service = CreateService(handler, Substitute.For<IGitHubTokenStore>());

        var start = await service.StartAsync(CancellationToken.None);

        AssertEx.Equal("WDJB-MJHT", start.UserCode);
        AssertEx.Equal("https://github.com/login/device", start.VerificationUri);
        AssertEx.Equal(expected: 900, start.ExpiresInSeconds);
        AssertEx.Equal(expected: 5, start.IntervalSeconds);
        AssertEx.Equal("dev-code-abc", start.DeviceCode);
    }

    [Test]
    public async Task Poll_WhenPendingThenAuthorized_StoresTokenAndReportsLogin()
    {
        using var handler = new CapturingHttpMessageHandler();
        // 1st poll: authorization_pending. 2nd poll: token. 3rd request: GET /user → login.
        handler.EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object> { ["error"] = "authorization_pending" }));
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        var service = CreateService(handler, tokenStore);

        var pending = await service.PollAsync("dev-code-abc", CancellationToken.None);
        AssertEx.Equal(GitHubDeviceFlowState.Pending, pending.State);
        await tokenStore.DidNotReceive().SetSessionAsync(Arg.Any<GitHubSession>(), Arg.Any<CancellationToken>());

        handler.EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = "ghu_authorized_token",
            ["token_type"] = "bearer",
            ["scope"] = "repo"
        }));
        handler.EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object> { ["login"] = "octocat" }));

        var authorized = await service.PollAsync("dev-code-abc", CancellationToken.None);

        AssertEx.Equal(GitHubDeviceFlowState.Authorized, authorized.State);
        AssertEx.Equal("octocat", authorized.Login);
        await tokenStore.Received(1).SetSessionAsync(
            Arg.Is<GitHubSession>(s => s.AccessToken == "ghu_authorized_token" && s.Login == "octocat"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Poll_WhenAccessDenied_ReportsDeniedAndStoresNothing()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object> { ["error"] = "access_denied" }));
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        var service = CreateService(handler, tokenStore);

        var poll = await service.PollAsync("dev-code-abc", CancellationToken.None);

        AssertEx.Equal(GitHubDeviceFlowState.Denied, poll.State);
        AssertEx.Null(poll.Login);
        await tokenStore.DidNotReceive().SetSessionAsync(Arg.Any<GitHubSession>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Poll_WhenExpiredToken_ReportsExpiredAndStoresNothing()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object> { ["error"] = "expired_token" }));
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        var service = CreateService(handler, tokenStore);

        var poll = await service.PollAsync("dev-code-abc", CancellationToken.None);

        AssertEx.Equal(GitHubDeviceFlowState.Expired, poll.State);
        await tokenStore.DidNotReceive().SetSessionAsync(Arg.Any<GitHubSession>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SignOut_RevokesServerSide_ThenClearsLocal()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.NoContent, json: "");
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        tokenStore.GetSessionAsync(Arg.Any<CancellationToken>()).Returns(new GitHubSession("ghu_secret_token", "octocat"));
        var service = CreateService(handler, tokenStore);

        await service.SignOutAsync(CancellationToken.None);

        // The best-effort server-side revoke DELETE is attempted before the local store is cleared (the DELETE is a
        // no-op at GitHub without the App client_secret, but the call is still issued).
        AssertEx.Equal(expected: 1, handler.Requests.Count);
        AssertEx.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        AssertEx.True(handler.Requests[0].Uri!.AbsoluteUri.Contains("/applications/", StringComparison.Ordinal),
            "sign-out must DELETE the application token (server-side revoke)");
        await tokenStore.Received(1).ClearSessionAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SignOut_WhenRemoteRevokeFails_StillClearsLocal()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, json: "");
        var tokenStore = Substitute.For<IGitHubTokenStore>();
        tokenStore.GetSessionAsync(Arg.Any<CancellationToken>()).Returns(new GitHubSession("ghu_secret_token", "octocat"));
        var service = CreateService(handler, tokenStore);

        await service.SignOutAsync(CancellationToken.None);

        await tokenStore.Received(1).ClearSessionAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Poll_WhenTokenEndpointFails_DoesNotLogTokenMaterial()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["access_token"] = "ghu_secret_should_not_log",
            ["token_type"] = "bearer"
        }));
        // GET /user fails → login resolves to "unknown", token still stored; assert it never appears in the log.
        handler.EnqueueJson(HttpStatusCode.InternalServerError, json: "");
        var logger = new CapturingLogger<GitHubAuthService>();
        var service = CreateService(handler, Substitute.For<IGitHubTokenStore>(), logger);

        await service.PollAsync("dev-code-abc", CancellationToken.None);

        AssertEx.False(logger.AllText.Contains("ghu_secret_should_not_log", StringComparison.Ordinal),
            "the access token must never be logged");
    }

    private GitHubAuthService CreateService(CapturingHttpMessageHandler handler,
        IGitHubTokenStore tokenStore,
        ILogger<GitHubAuthService>? logger = null)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false);
        _disposables.Add(httpClient);
        var options = Options.Create(new AppUpdateChannelOptions
        {
            Channel = "tester",
            GitHubRepositoryUrl = "https://github.com/example/tester-repo",
            GitHubAppClientId = ClientId
        });

        return new GitHubAuthService(httpClient, tokenStore, options, logger ?? NullLogger<GitHubAuthService>.Instance);
    }
}
