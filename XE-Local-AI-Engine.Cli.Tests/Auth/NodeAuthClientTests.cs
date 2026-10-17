namespace XE_Local_AI_Engine.Cli.Tests.Auth;

using System.Net;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Auth;
using XE_Local_AI_Engine.Cli.Tests.Testing;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

[Category(TestCategories.Unit)]
public sealed class NodeAuthClientTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:5123/");

    [Test]
    public async Task AnApiCall_WhenTheTokenIsRejectedAndTheRefreshIsDead_RefreshesThroughTheHeaderThenSignsInAgainAndRetriesOnce()
    {
        var time = new ManualTimeProvider();
        using var handler = new ScriptedHttpHandler(request => (request.Method, request.Path, request.Bearer) switch
        {
            ("POST", "/api/local/v1/auth/login", _) when request.Body!.Contains("\"password\":\"first\"", StringComparison.Ordinal)
                => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", time.GetUtcNow().AddHours(1), "refresh-1")),
            ("POST", "/api/local/v1/auth/login", _)
                => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-2", time.GetUtcNow().AddHours(1), "refresh-2")),
            ("POST", "/api/local/v1/auth/refresh", _) => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            ("GET", "/api/local/v1/agents", "access-2") => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"6f1c0a4e-0000-4000-8000-000000000001","name":"Coder"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        });
        var asked = 0;
        using var http = Client(handler);
        using var auth = new NodeAuthClient(http, Origin, time, tokenFilePath: null, _ =>
        {
            asked++;
            return Task.FromResult<string?>("second");
        });
        await auth.LoginAsync("first", email: null, CancellationToken.None);

        var agents = await new NodeApiClient(http, auth).ListAgentsAsync(CancellationToken.None);

        AssertEx.Equal("Coder", agents.Items.Single().Name);
        AssertEx.Equal(1, asked);
        AssertEx.Equal("POST /api/local/v1/auth/login,GET /api/local/v1/agents,POST /api/local/v1/auth/refresh,POST /api/local/v1/auth/login,GET /api/local/v1/agents",
            string.Join(',', handler.Requests));
        var refresh = handler.Requests[2];
        AssertEx.Equal("refresh-1", refresh.RefreshHeader);
        AssertEx.False(refresh.HasCookie, "a body-mode client never sends a cookie");
        AssertEx.Contains(handler.Requests[0].Body, "\"returnRefreshToken\":true");
        AssertEx.Equal("access-1", handler.Requests[1].Bearer);
    }

    [Test]
    public async Task GetAccessTokenAsync_WhenTheTokenIsAboutToExpire_RefreshesThroughTheHeaderFirst()
    {
        var time = new ManualTimeProvider();
        using var handler = new ScriptedHttpHandler(request => request.Path switch
        {
            "/api/local/v1/auth/login" => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", time.GetUtcNow().AddMinutes(1), "refresh-1")),
            _ => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-2", time.GetUtcNow().AddMinutes(15), "refresh-2"))
        });
        using var http = Client(handler);
        using var auth = new NodeAuthClient(http, Origin, time, tokenFilePath: null, passwordProvider: null);
        await auth.LoginAsync("pw", email: null, CancellationToken.None);

        var beforeSkew = await auth.GetAccessTokenAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(31));
        var insideSkew = await auth.GetAccessTokenAsync(CancellationToken.None);

        AssertEx.Equal("access-1", beforeSkew);
        AssertEx.Equal("access-2", insideSkew);
        AssertEx.Equal("refresh-1", handler.Requests.Single(request => request.Path == "/api/local/v1/auth/refresh").RefreshHeader);
    }

    [Test]
    public async Task GetAccessTokenAsync_WhenNothingIsStoredAndNoPasswordProviderIsGiven_FailsAsUnauthorized()
    {
        using var handler = new ScriptedHttpHandler(_ => throw new InvalidOperationException("no request is expected"));
        using var http = Client(handler);
        using var auth = new NodeAuthClient(http, Origin, new ManualTimeProvider(), tokenFilePath: null, passwordProvider: null);

        var failure = await AssertEx.ThrowsAsync<NodeApiException>(() => auth.GetAccessTokenAsync(CancellationToken.None));

        AssertEx.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        AssertEx.Empty(handler.Requests);
    }

    [Test]
    public async Task LoginAsync_WhenTheNodeLocksTheAccountOut_SurfacesItsMessage()
    {
        using var handler = new ScriptedHttpHandler(_ => ScriptedHttpHandler.Json(HttpStatusCode.Unauthorized,
            """{"message":"Too many failed sign-ins. Try again in 60 seconds.","code":"locked-out","retryAfterSeconds":60}"""));
        using var http = Client(handler);
        using var auth = new NodeAuthClient(http, Origin, new ManualTimeProvider(), tokenFilePath: null, passwordProvider: null);

        var failure = await AssertEx.ThrowsAsync<NodeApiException>(() => auth.LoginAsync("pw", email: null, CancellationToken.None));

        AssertEx.Equal("Too many failed sign-ins. Try again in 60 seconds.", failure.Message);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task TheTokenFile_IsOwnerOnly_AndIsReusedOnlyByTheOriginThatIssuedIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xe-cli-config-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "xe", "tokens.json");
        try
        {
            var time = new ManualTimeProvider();
            using var issuing = new ScriptedHttpHandler(_ => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", time.GetUtcNow().AddHours(1), "refresh-1")));
            using (var http = Client(issuing))
            using (var auth = new NodeAuthClient(http, Origin, time, path, passwordProvider: null))
            {
                await auth.LoginAsync("pw", email: null, CancellationToken.None);
            }

            using var silent = new ScriptedHttpHandler(_ => throw new InvalidOperationException("a stored live token needs no request"));
            using var silentHttp = Client(silent);
            using var sameOrigin = new NodeAuthClient(silentHttp, Origin, time, path, passwordProvider: null);
            using var otherOrigin = new NodeAuthClient(silentHttp, new Uri("http://127.0.0.1:6000/"), time, path, passwordProvider: null);

            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            AssertEx.Equal("access-1", await sameOrigin.GetAccessTokenAsync(CancellationToken.None));
            _ = await AssertEx.ThrowsAsync<NodeApiException>(() => otherOrigin.GetAccessTokenAsync(CancellationToken.None));
            AssertEx.Empty(silent.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task GetAccessTokenAsync_WhenTheCachedRefreshIsRevoked_AdoptsANewerPairFromTheTokenFileBeforeAskingForAPassword(bool anotherProcessRotated)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xe-cli-config-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "tokens.json");
        try
        {
            var time = new ManualTimeProvider();
            using var handler = new ScriptedHttpHandler(request => (request.Path, request.RefreshHeader) switch
            {
                ("/api/local/v1/auth/login", _) when request.Body!.Contains("\"password\":\"first\"", StringComparison.Ordinal)
                    => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-1", time.GetUtcNow().AddMinutes(15), "refresh-1")),
                ("/api/local/v1/auth/login", _)
                    => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-login", time.GetUtcNow().AddMinutes(15), "refresh-login")),
                ("/api/local/v1/auth/refresh", "refresh-2")
                    => ScriptedHttpHandler.Json(HttpStatusCode.OK, ScriptedHttpHandler.Tokens("access-3", time.GetUtcNow().AddMinutes(15), "refresh-3")),
                _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            });
            var asked = 0;
            using var http = Client(handler);
            using var auth = new NodeAuthClient(http, Origin, time, path, _ =>
            {
                asked++;
                return Task.FromResult<string?>("second");
            });
            await auth.LoginAsync("first", email: null, CancellationToken.None);
            if (anotherProcessRotated)
            {
                // The other process refreshed with refresh-1 and saved its new pair; the node then revoked refresh-1.
                await TokenFileStore.SaveAsync(path,
                    new TokenPair
                    {
                        AccessToken = "access-2",
                        ExpiresAtUtc = time.GetUtcNow().AddMinutes(15),
                        RefreshToken = "refresh-2",
                        Origin = Origin.GetLeftPart(UriPartial.Authority)
                    },
                    CancellationToken.None);
            }

            time.Advance(TimeSpan.FromMinutes(20));
            var token = await auth.GetAccessTokenAsync(CancellationToken.None);

            var refreshes = string.Join(',', handler.Requests.Where(static request => request.Path == "/api/local/v1/auth/refresh").Select(static request => request.RefreshHeader));
            var stored = await TokenFileStore.LoadAsync(path, Origin, CancellationToken.None);
            if (anotherProcessRotated)
            {
                AssertEx.Equal("access-3", token);
                AssertEx.Equal(0, asked, "a valid pair in the file needs no password");
                AssertEx.Equal("refresh-1,refresh-2", refreshes);
                AssertEx.Equal("refresh-3", stored?.RefreshToken);
            }
            else
            {
                AssertEx.Equal("access-login", token);
                AssertEx.Equal(1, asked);
                AssertEx.Equal("refresh-1", refreshes, "the same rejected pair in the file is never retried");
                AssertEx.Equal("refresh-login", stored?.RefreshToken);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task UnlockVaultAsync_WaitsThroughTheHostSwapUntilTheNodeAnswersUnlocked()
    {
        var time = new ManualTimeProvider();
        var statuses = new Queue<Func<HttpResponseMessage>>([
            () => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"setupRequired":false,"authenticated":false,"vault":"locked"}"""),
            () => throw new HttpRequestException("connection refused"),
            () => ScriptedHttpHandler.Json(HttpStatusCode.ServiceUnavailable, """{"title":"Starting"}"""),
            () => ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"setupRequired":false,"authenticated":false,"vault":"unlocked"}""")
        ]);
        using var handler = new ScriptedHttpHandler(request => request.Path == "/api/local/v1/auth/vault/unlock"
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : statuses.Dequeue()());
        using var http = Client(handler);
        using var auth = new NodeAuthClient(http, Origin, time, tokenFilePath: null, passwordProvider: null);

        var unlock = auth.UnlockVaultAsync("pw", CancellationToken.None);
        for (var probe = 0; probe < 4; probe++)
        {
            await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, TimeSpan.FromSeconds(10), $"probe {probe} delay armed");
            time.Advance(NodeAuthClient.UnlockPollInterval);
        }

        await AssertEx.CompletesAsync(unlock, TimeSpan.FromSeconds(10), "the unlock returns once the node answers unlocked");
        AssertEx.Empty(statuses);
        AssertEx.Contains(handler.Requests[0].Body, "\"password\":\"pw\"");
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(Origin, NodeHttp.ApiPrefix)
        };
}
