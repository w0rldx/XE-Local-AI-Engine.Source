namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the Codex OAuth login / refresh mechanics: PKCE challenge/verifier binding, callback
///     state validation, code→token exchange, refresh, the <see cref="ICodexAuthService.BeginLogin" /> URL exposure,
///     loopback timeout, and the never-logs-token-material guarantee. The token endpoint is mocked; the loopback
///     listener is real and driven by an in-test HTTP client posing as the OAuth redirect.
/// </summary>
public sealed class CodexAuthServiceTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Test]
    public async Task BeginLogin_DoesNotLaunchSystemBrowser()
    {
        var source = await File.ReadAllTextAsync(GetProviderPath("Auth", "CodexAuthService.cs"));

        AssertEx.False(source.Contains("Process.Start", StringComparison.Ordinal),
            "Codex OAuth login must return the authorize URL to the React client without launching the system browser.");
        AssertEx.False(source.Contains("UseShellExecute = true", StringComparison.Ordinal),
            "Codex OAuth login must not shell-execute the authorize URL.");
    }

    [Test]
    public async Task RefreshAsync_PostsRefreshGrant_AndPersistsRotatedSession()
    {
        using var handler = new CapturingHttpMessageHandler();
        var newAccess = CodexTestHelpers.BuildAccountJwt();
        handler.EnqueueJson(HttpStatusCode.OK, CodexTestHelpers.BuildTokenResponse(newAccess, "rotated-refresh"));
        var tokenStore = Substitute.For<ICodexTokenStore>();
        var service = CreateService(handler, tokenStore, out _);
        var current = new CodexTokens("old-access", "old-refresh", DateTimeOffset.UtcNow, CodexTestHelpers.AccountId);

        var refreshed = await service.RefreshAsync(current);

        AssertEx.Equal(newAccess, refreshed.AccessToken);
        AssertEx.Equal("rotated-refresh", refreshed.RefreshToken);
        AssertEx.Equal(CodexTestHelpers.AccountId, refreshed.AccountId);
        AssertEx.Contains(handler.Requests[0].Body, "refresh_token");
        AssertEx.Contains(handler.Requests[0].Body, "grant_type");
        await tokenStore.Received(1).SaveAsync(Arg.Is<CodexTokens>(t => t.RefreshToken == "rotated-refresh"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BeginLogin_ExposesAuthorizeUrl_WithPkceS256ChallengeAndState()
    {
        using var handler = new CapturingHttpMessageHandler();
        // Short timeout so the background completion ends promptly and the loopback listener is released.
        var service = CreateService(handler, Substitute.For<ICodexTokenStore>(), out var options, TimeSpan.FromMilliseconds(150));

        var handle = service.BeginLogin();
        var query = ParseQuery(handle.AuthorizeUrl.Query);

        AssertEx.Equal("S256", query["code_challenge_method"]);
        AssertEx.NotNullOrEmpty(query["code_challenge"]);
        AssertEx.NotNullOrEmpty(query["state"]);
        AssertEx.Equal(options.RedirectUri.ToString(), query["redirect_uri"]);
        // A SHA-256 base64url challenge is 43 chars with no padding.
        AssertEx.Equal(expected: 43, query["code_challenge"].Length);
        AssertEx.False(query["code_challenge"].Contains(value: '=', StringComparison.Ordinal), "challenge must be base64url (no padding)");

        // LIVE-CORRECTNESS (verified against the working opencode reference client):
        // the authorize host is the OAuth issuer auth.openai.com, and the simplified-flow params are present.
        AssertEx.Equal("auth.openai.com", handle.AuthorizeUrl.Host);
        AssertEx.Equal(options.Originator, query["originator"]);
        AssertEx.Equal("true", query["id_token_add_organizations"]);
        AssertEx.Equal("true", query["codex_cli_simplified_flow"]);
        AssertEx.Equal(options.Scope, query["scope"]);

        // Drain the background completion (faults on timeout) so the listener is freed before the test exits.
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => handle.Completion);
    }

    [Test]
    public async Task BeginLogin_WhenCallbackDeliversCode_ExchangesWithMatchingVerifier_AndPersistsSession()
    {
        using var handler = new CapturingHttpMessageHandler();
        var access = CodexTestHelpers.BuildAccountJwt();
        handler.EnqueueJson(HttpStatusCode.OK, CodexTestHelpers.BuildTokenResponse(access, "refresh-1"));
        var tokenStore = Substitute.For<ICodexTokenStore>();
        var service = CreateService(handler, tokenStore, out var options);

        var handle = service.BeginLogin();
        var query = ParseQuery(handle.AuthorizeUrl.Query);
        var state = query["state"];
        var challenge = query["code_challenge"];

        // Pose as the OAuth provider redirecting to the loopback callback with a code + the original state.
        await DeliverCallbackAsync(options, $"code=auth-code-xyz&state={Uri.EscapeDataString(state)}");

        var tokens = await handle.Completion;

        AssertEx.Equal(access, tokens.AccessToken);
        AssertEx.Equal(CodexTestHelpers.AccountId, tokens.AccountId);
        await tokenStore.Received(1).SaveAsync(Arg.Any<CodexTokens>(), Arg.Any<CancellationToken>());

        // The exchange request must carry the verifier whose SHA-256 base64url equals the authorize challenge (PKCE binding).
        var exchangeBody = handler.Requests[0].Body;
        AssertEx.Contains(exchangeBody, "auth-code-xyz");
        var verifier = ExtractJsonString(exchangeBody, "code_verifier");
        var recomputed = CodexTestHelpers.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        AssertEx.Equal(challenge, recomputed);
    }

    [Test]
    public async Task BeginLogin_WhenCallbackStateDoesNotMatch_FailsLogin()
    {
        using var handler = new CapturingHttpMessageHandler();
        var service = CreateService(handler, Substitute.For<ICodexTokenStore>(), out var options);

        var handle = service.BeginLogin();

        await DeliverCallbackAsync(options, "code=auth-code-xyz&state=not-the-expected-state");

        await AssertEx.ThrowsAsync<CodexAuthException>(() => handle.Completion);
    }

    [Test]
    public async Task BeginLogin_WhenNoCallbackArrivesBeforeTimeout_FailsWithCancellation()
    {
        using var handler = new CapturingHttpMessageHandler();
        var service = CreateService(handler, Substitute.For<ICodexTokenStore>(), out _, TimeSpan.FromMilliseconds(150));

        var handle = service.BeginLogin();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => handle.Completion);
    }

    [Test]
    public async Task RefreshAsync_WhenTokenEndpointFails_ThrowsWithoutLoggingTokenMaterial()
    {
        using var handler = new CapturingHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.BadRequest, json: """{"error":"invalid_grant"}""");
        var logger = new CapturingLogger<CodexAuthService>();
        var service = CreateService(handler, Substitute.For<ICodexTokenStore>(), out _, logger: logger);
        var current = new CodexTokens("secret-access-abc", "secret-refresh-def", DateTimeOffset.UtcNow, CodexTestHelpers.AccountId);

        await AssertEx.ThrowsAsync<CodexAuthException>(() => service.RefreshAsync(current));

        AssertEx.False(logger.AllText.Contains("secret-access-abc", StringComparison.Ordinal), "access token must never be logged");
        AssertEx.False(logger.AllText.Contains("secret-refresh-def", StringComparison.Ordinal), "refresh token must never be logged");
    }

    private CodexAuthService CreateService(CapturingHttpMessageHandler handler,
        ICodexTokenStore tokenStore,
        out CodexOptions options,
        TimeSpan? loginTimeout = null,
        ILogger<CodexAuthService>? logger = null)
    {
        options = new CodexOptions
        {
            CallbackPort = GetFreeLoopbackPort(),
            LoginTimeout = loginTimeout ?? TimeSpan.FromSeconds(10),
            TokenRequestTimeout = TimeSpan.FromSeconds(10)
        };

        // The test owns the handler (via `using`); this client must not dispose it.
        var httpClient = new HttpClient(handler, disposeHandler: false);
        _disposables.Add(httpClient);
        return new CodexAuthService(Options.Create(options), httpClient, tokenStore, logger ?? NullLogger<CodexAuthService>.Instance);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split(separator: '&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf(value: '=', StringComparison.Ordinal);
            if (index < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..index]);
            var value = Uri.UnescapeDataString(pair[(index + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static async Task DeliverCallbackAsync(CodexOptions options, string queryString)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var callbackUri = new Uri($"http://localhost:{options.CallbackPort}{options.CallbackPath}?{queryString}");

        // Retry briefly: the loopback listener may still be coming up when the callback fires.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(callbackUri);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(20);
            }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ExtractJsonString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString()!;
    }

    private static string GetProviderPath(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[]
            {
                directory.FullName,
                "XE-Local-AI-Engine.Providers.CodexOAuth"
            }.Concat(relativePath).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate XE-Local-AI-Engine.Providers.CodexOAuth.");
    }
}
